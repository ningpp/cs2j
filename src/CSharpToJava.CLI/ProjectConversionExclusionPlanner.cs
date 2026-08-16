using System.Xml.Linq;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.CLI;

internal sealed class ProjectConversionExclusion
{
    public required string ProjectPath { get; init; }
    public required string ProjectName { get; init; }
    public required string Reason { get; init; }
}

internal static class ProjectConversionExclusionPlanner
{
    public static IReadOnlyDictionary<string, ProjectConversionExclusion> Plan(
        ProjectGraph graph,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return PlanCore(graph.ProjectsInTopologicalOrder.Select(project =>
            new ProjectNodeInfo(project.ProjectFilePath, project.Name, project.ProjectReferences)),
            compatNamespacePrefixes);
    }

    public static IReadOnlyDictionary<string, ProjectConversionExclusion> Plan(
        IReadOnlyList<WorkspaceProject> projects,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return PlanCore(projects.Select(project =>
            new ProjectNodeInfo(project.FilePath, project.Name, project.ProjectReferences)),
            compatNamespacePrefixes);
    }

    /// <summary>
    /// Evaluates standalone project files (not necessarily part of the loaded
    /// solution graph) so unsupported projects that live under the source root but
    /// are absent from the .sln are excluded from intermediate copies too.
    /// </summary>
    public static IReadOnlyDictionary<string, ProjectConversionExclusion> PlanFiles(
        IEnumerable<string> projectFiles,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
    {
        var exclusions = new Dictionary<string, ProjectConversionExclusion>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (exclusions.ContainsKey(projectFile))
            {
                continue;
            }

            var evaluation = EvaluateProjectSupport(projectFile, compatNamespacePrefixes);
            if (!evaluation.IsSupported)
            {
                exclusions[projectFile] = new ProjectConversionExclusion
                {
                    ProjectPath = projectFile,
                    ProjectName = Path.GetFileNameWithoutExtension(projectFile),
                    Reason = evaluation.Reason,
                };
            }
        }

        return exclusions;
    }

    private static IReadOnlyDictionary<string, ProjectConversionExclusion> PlanCore(
        IEnumerable<ProjectNodeInfo> projects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        var nodes = projects
            .Select(project => new ProjectNodeInfo(
                Path.GetFullPath(project.ProjectPath),
                project.ProjectName,
                project.ProjectReferences.Select(Path.GetFullPath).ToList()))
            .GroupBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase);

        var reverseDependencies = nodes.Values.ToDictionary(
            project => project.ProjectPath,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var project in nodes.Values)
        {
            foreach (var dependencyPath in project.ProjectReferences)
            {
                if (reverseDependencies.TryGetValue(dependencyPath, out var dependents))
                {
                    dependents.Add(project.ProjectPath);
                }
            }
        }

        var exclusions = new Dictionary<string, ProjectConversionExclusion>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var project in nodes.Values)
        {
            var evaluation = EvaluateProjectSupport(project.ProjectPath, compatNamespacePrefixes);
            if (evaluation.IsSupported)
            {
                continue;
            }

            exclusions[project.ProjectPath] = new ProjectConversionExclusion
            {
                ProjectPath = project.ProjectPath,
                ProjectName = project.ProjectName,
                Reason = evaluation.Reason,
            };

            queue.Enqueue(project.ProjectPath);
        }

        while (queue.Count > 0)
        {
            var excludedProjectPath = queue.Dequeue();
            if (!reverseDependencies.TryGetValue(excludedProjectPath, out var dependents))
            {
                continue;
            }

            foreach (var dependentPath in dependents)
            {
                if (exclusions.ContainsKey(dependentPath) || !nodes.TryGetValue(dependentPath, out var dependentProject))
                {
                    continue;
                }

                exclusions[dependentPath] = new ProjectConversionExclusion
                {
                    ProjectPath = dependentPath,
                    ProjectName = dependentProject.ProjectName,
                    Reason = $"Depends on excluded project '{nodes[excludedProjectPath].ProjectName}'",
                };

                queue.Enqueue(dependentPath);
            }
        }

        return exclusions;
    }

    private static ProjectSupportEvaluation EvaluateProjectSupport(
        string projectPath,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        try
        {
            var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var reasons = new List<string>();
            var requiredCompatNamespaces = new HashSet<string>(StringComparer.Ordinal);
            var hasNeverSupportedReason = false;

            var sdk = doc.Root?.Attribute("Sdk")?.Value;
            if (!string.IsNullOrWhiteSpace(sdk)
                && sdk.Contains("Microsoft.NET.Sdk.WindowsDesktop", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("WindowsDesktop SDK");
                hasNeverSupportedReason = true;
                requiredCompatNamespaces.Add("System.Windows.Forms");
                requiredCompatNamespaces.Add("System.Windows");
            }

            var useWpf = GetBooleanProperty(doc, "UseWPF");
            if (useWpf)
            {
                reasons.Add("UseWPF=true");
                hasNeverSupportedReason = true;
                requiredCompatNamespaces.Add("System.Windows");
            }

            if (GetBooleanProperty(doc, "UseWindowsForms"))
            {
                reasons.Add("UseWindowsForms=true");
                hasNeverSupportedReason = true;
                requiredCompatNamespaces.Add("System.Windows.Forms");
            }

            // GenAPI-style ref assemblies (e.g. dotnet/runtime ref\*.csproj) are
            // API-surface stubs: every public type is re-declared with `throw null`
            // bodies. Converting them duplicates all Java types, and merging them
            // into the semantic compilation corrupts DeclaringSyntaxReferences of
            // partial types (cross-tree references crash the converters).
            var assemblyName = GetPropertyValues(doc, "AssemblyName").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(assemblyName)
                && assemblyName.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"Ref assembly (AssemblyName={assemblyName})");
                hasNeverSupportedReason = true;
            }

            var targetFrameworks = GetPropertyValues(doc, "TargetFramework")
                .Concat(GetPropertyValues(doc, "TargetFrameworks")
                    .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var windowsTargetFramework = targetFrameworks.FirstOrDefault(IsWindowsOnlyTargetFramework);
            if (!string.IsNullOrWhiteSpace(windowsTargetFramework))
            {
                reasons.Add($"TargetFramework={windowsTargetFramework}");
                if (windowsTargetFramework.StartsWith("uap", StringComparison.OrdinalIgnoreCase))
                {
                    hasNeverSupportedReason = true;
                }
                else
                {
                    hasNeverSupportedReason = true;
                    requiredCompatNamespaces.Add("System.Windows.Forms");
                    requiredCompatNamespaces.Add("System.Windows");
                }
            }

            var targetPlatformIdentifier = GetPropertyValues(doc, "TargetPlatformIdentifier")
                .FirstOrDefault(IsWindowsOnlyTargetPlatformIdentifier);
            if (!string.IsNullOrWhiteSpace(targetPlatformIdentifier))
            {
                reasons.Add("TargetPlatformIdentifier=" + targetPlatformIdentifier);
                if (targetPlatformIdentifier.Equals("UAP", StringComparison.OrdinalIgnoreCase))
                {
                    hasNeverSupportedReason = true;
                }
                else
                {
                    hasNeverSupportedReason = true;
                    requiredCompatNamespaces.Add("System.Windows.Forms");
                    requiredCompatNamespaces.Add("System.Windows");
                }
            }

            var usesUwpPackage = doc
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Any(value => string.Equals(value, "Microsoft.NETCore.UniversalWindowsPlatform", StringComparison.OrdinalIgnoreCase));
            if (usesUwpPackage)
            {
                reasons.Add("UniversalWindowsPlatform package");
                hasNeverSupportedReason = true;
            }

            if (reasons.Count == 0)
            {
                return ProjectSupportEvaluation.Supported;
            }

            // A Windows-only project is convertible when every platform namespace it
            // may rely on has an explicit compat namespace mapping in the type-mapping
            // config (e.g. System.Windows.Forms → dotnet.winforms.swing). Ref assemblies
            // and UWP have no compat layer, so they stay excluded regardless.
            if (!hasNeverSupportedReason
                && requiredCompatNamespaces.Count > 0
                && requiredCompatNamespaces.All(ns => HasExplicitCompatNamespaceMapping(ns, compatNamespacePrefixes)))
            {
                return ProjectSupportEvaluation.Supported;
            }

            return new ProjectSupportEvaluation(false, "Windows-only project: " + string.Join(", ", reasons));
        }
        catch (Exception ex)
        {
            return new ProjectSupportEvaluation(false, $"Failed to inspect project file: {ex.Message}");
        }
    }

    private static bool HasExplicitCompatNamespaceMapping(
        string namespaceName,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        return compatNamespacePrefixes != null
            && compatNamespacePrefixes.Contains(namespaceName, StringComparer.Ordinal);
    }

    private static IEnumerable<string> GetPropertyValues(XDocument projectDoc, string propertyName)
    {
        return projectDoc
            .Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => element.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    private static bool GetBooleanProperty(XDocument projectDoc, string propertyName)
    {
        return GetPropertyValues(projectDoc, propertyName)
            .Any(value => bool.TryParse(value, out var result) && result);
    }

    private static bool IsWindowsOnlyTargetFramework(string tfm)
    {
        return tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("uap", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) && tfm.Contains("windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsOnlyTargetPlatformIdentifier(string identifier)
    {
        return identifier.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("UAP", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectNodeInfo(string ProjectPath, string ProjectName, IReadOnlyList<string> ProjectReferences);

    private sealed record ProjectSupportEvaluation(bool IsSupported, string Reason)
    {
        public static ProjectSupportEvaluation Supported { get; } = new(true, string.Empty);
    }
}
