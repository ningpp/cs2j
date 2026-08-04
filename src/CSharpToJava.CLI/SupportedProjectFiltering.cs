using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.CLI;

/// <summary>
/// Shared logic that, given a discovered project graph, decides which projects the
/// converter supports and lets the preprocessors copy/transform ONLY those projects.
/// It also rewrites a copied <c>.sln</c> so that it no longer references the
/// unsupported (excluded) projects, producing a self-consistent buildable tree.
/// </summary>
internal static class SupportedProjectFiltering
{
    /// <summary>
    /// Result of computing which projects must be excluded from the copied tree.
    /// </summary>
    internal sealed class ExclusionInfo
    {
        /// <summary>Full paths of the excluded .csproj files.</summary>
        public HashSet<string> ExcludedProjectPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Full directory paths of the excluded projects.</summary>
        public HashSet<string> ExcludedProjectDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Human-readable exclusion reasons keyed by project name.</summary>
        public List<ProjectConversionExclusion> Exclusions { get; } = new();

        public bool HasExclusions => ExcludedProjectPaths.Count > 0;
    }

    /// <summary>
    /// Computes the set of unsupported projects (and their directories) for a graph.
    /// Returns an empty <see cref="ExclusionInfo"/> when the graph is null or everything
    /// is supported.
    /// </summary>
    public static ExclusionInfo ComputeExclusions(ProjectGraph? graph, bool verbose)
    {
        var info = new ExclusionInfo();
        if (graph == null || graph.ProjectsInTopologicalOrder.Count == 0)
        {
            return info;
        }

        IReadOnlyDictionary<string, ProjectConversionExclusion> exclusions;
        try
        {
            exclusions = ProjectConversionExclusionPlanner.Plan(graph);
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Warning: failed to evaluate project support, keeping all projects: {ex.Message}");
            }
            return info;
        }

        if (exclusions.Count == 0)
        {
            return info;
        }

        foreach (var exclusion in exclusions.Values)
        {
            info.Exclusions.Add(exclusion);
            info.ExcludedProjectPaths.Add(Path.GetFullPath(exclusion.ProjectPath));

            var directory = Path.GetDirectoryName(exclusion.ProjectPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                info.ExcludedProjectDirectories.Add(Path.GetFullPath(directory));
            }
        }

        if (verbose)
        {
            Console.WriteLine($"Preprocessing excludes {info.Exclusions.Count} unsupported project(s):");
            foreach (var exclusion in info.Exclusions.OrderBy(e => e.ProjectName, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Skipped: {exclusion.ProjectName} ({exclusion.Reason})");
            }
        }

        return info;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> lives inside any excluded project directory.
    /// </summary>
    public static bool IsUnderExcludedProject(string fullPath, ExclusionInfo info)
    {
        if (!info.HasExclusions)
        {
            return false;
        }

        return IsUnderAnyDirectory(fullPath, info.ExcludedProjectDirectories);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is the same as, or a descendant of, any
    /// directory in <paramref name="directories"/>.
    /// </summary>
    public static bool IsUnderAnyDirectory(string fullPath, IReadOnlyCollection<string> directories)
    {
        if (directories.Count == 0)
        {
            return false;
        }

        var path = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var directory in directories)
        {
            var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(path, dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Regex SolutionProjectLineRegex = new(
        "^Project\\(\"(?<typeGuid>[^\"]+)\"\\)\\s*=\\s*\"(?<name>[^\"]*)\",\\s*\"(?<path>[^\"]*)\",\\s*\"(?<projectGuid>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites solution file content so that project blocks whose <c>.csproj</c> resolves
    /// to an excluded project are removed, along with their configuration entries in the
    /// <c>Global</c> section. Non-.csproj entries (solution folders) are preserved.
    /// </summary>
    public static string RewriteSolutionContent(
        string solutionContent,
        string solutionDirectory,
        ExclusionInfo info)
    {
        if (!info.HasExclusions || string.IsNullOrWhiteSpace(solutionContent))
        {
            return solutionContent;
        }

        var lines = solutionContent.Split('\n');
        var output = new StringBuilder();
        var droppedGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var inDroppedBlock = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (inDroppedBlock)
            {
                // A project block terminates with a bare "EndProject" (nested
                // sections terminate with "EndProjectSection", so they do not
                // end the block prematurely).
                if (trimmed.Equals("EndProject", StringComparison.OrdinalIgnoreCase))
                {
                    inDroppedBlock = false;
                }
                continue;
            }

            var match = SolutionProjectLineRegex.Match(trimmed);
            if (match.Success)
            {
                var relativePath = match.Groups["path"].Value;
                if (relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    string fullProjectPath;
                    try
                    {
                        fullProjectPath = Path.GetFullPath(Path.Combine(
                            solutionDirectory,
                            relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                    }
                    catch
                    {
                        fullProjectPath = string.Empty;
                    }

                    if (info.ExcludedProjectPaths.Contains(fullProjectPath) ||
                        SupportedProjectFiltering.IsUnderAnyDirectory(fullProjectPath, info.ExcludedProjectDirectories))
                    {
                        // Drop the whole block and remember its GUID so we can also
                        // strip its Global-section configuration lines.
                        droppedGuids.Add(match.Groups["projectGuid"].Value.Trim('{', '}'));
                        inDroppedBlock = true;
                        // If this is a single-line block ("... EndProject" is separate),
                        // there is nothing else to emit for it.
                        continue;
                    }
                }
            }

            // In the Global section, drop configuration/nesting lines that reference a
            // dropped project GUID. These lines begin with "{guid}".
            if (droppedGuids.Count > 0 && trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var guidEnd = trimmed.IndexOf('}');
                if (guidEnd > 0)
                {
                    var guid = trimmed.Substring(1, guidEnd - 1);
                    if (droppedGuids.Contains(guid))
                    {
                        continue;
                    }
                }
            }

            output.Append(line).Append('\n');
        }

        // Preserve the original trailing-newline behaviour: the split above always yields
        // a final empty element when the content ends with a newline, which we re-appended.
        return output.ToString();
    }
}
