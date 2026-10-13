using System.Xml.Linq;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.SupportLibraries;

/// <summary>
/// Shared Cs2jArray support-library release/project-reference wiring used by
/// the array-method and index-range passes: own-release copy identification,
/// name collision, library release, host-project computation, ProjectReference
/// injection and TFM warning.
/// </summary>
internal static class Cs2jArraySupportLibrary
{
    /// <summary>
    /// Releases the library (unless a copy already exists from an earlier
    /// pass) and injects a ProjectReference into every project that compiles
    /// a rewritten file.
    /// </summary>
    public static async Task EnsureSupportAsync(
        PassTreeContext tree,
        IReadOnlyList<string> rewrittenSourceFiles,
        Action<string, string> emitWarning)
    {
        if (rewrittenSourceFiles.Count == 0)
            return;

        var libProjectPath = Path.Combine(
            tree.IntermediateRoot,
            ArrayHelperSupportFiles.LibDirectoryName,
            Cs2jArrayProject.ProjectFileName);

        if (!File.Exists(libProjectPath))
        {
            ThrowOnCs2jArrayNameCollision(tree.Layout);
            await ArrayHelperSupportFiles.ReleaseAsync(
                Path.Combine(
                    tree.IntermediateRoot,
                    ArrayHelperSupportFiles.LibDirectoryName));
        }

        // Layout projects and SourceFilePaths live in the source tree in
        // legacy mode and inside the (equal) unified tree in unified mode, so
        // the source root is the correct boundary for props evaluation.
        var hostProjectDirectories = ComputeHostProjectDirectories(
            tree.Layout, tree.Exclusion, rewrittenSourceFiles, tree.ProjectGraph,
            tree.SourceRoot);

        EnsureProjectReferences(
            tree.SourceRoot,
            tree.IntermediateRoot,
            tree.Layout,
            tree.Exclusion,
            hostProjectDirectories,
            libProjectPath,
            emitWarning);
    }


    private static void ThrowOnCs2jArrayNameCollision(
        ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jArrayProject.IsCs2jArrayProject(p)
            && !IsOwnReleasedArrayProjectFile(p));
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jArrayProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j array helper support library; rename the project and retry.");
        }
    }

    private static bool IsOwnReleasedArrayProjectFile(string projectFilePath)
    {
        try
        {
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(ArrayHelperSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    internal static List<string> FindOwnReleaseDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(
                sourceRoot, ArrayHelperSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedArrayProjectFile(
                Path.Combine(dir, Cs2jArrayProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    internal static bool IsUnderOwnRelease(
        string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(
                dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(
                dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        string libProjectPath,
        Action<string, string> emitWarning)
    {
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file =>
                         file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(
                    projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
                continue;

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureProjectReference(
                destinationProjectFile,
                libProjectPath,
                intermediateRoot,
                emitWarning);
        }
    }

    private static void EnsureProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        Action<string, string> emitWarning)
    {
        if (!File.Exists(projectFilePath))
            return;

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null
            || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(
            root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(
                enableDefaultCompileItems?.Trim(),
                "false",
                StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(
                ArrayHelperSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals(
                        (string?)e.Attribute("Remove"),
                        removePattern,
                        StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement(
                        "ItemGroup",
                        new XElement(
                            "Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
                continue;

            var resolvedPath = Path.GetFullPath(
                Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
                referenceElement.Remove();
        }

        var relativeInclude = Path.GetRelativePath(
            projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement(
                "ItemGroup",
                new XElement(
                    "ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        WarnWhenTargetFrameworkBelowNet10(
            projectFilePath, root, emitWarning);

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static void WarnWhenTargetFrameworkBelowNet10(
        string projectFilePath,
        XElement root,
        Action<string, string> emitWarning)
    {
        var tfm = ProjectUnsignedTypePreprocessor.GetFirstTargetFramework(root);
        if (ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(tfm))
            return;

        emitWarning(
            projectFilePath,
            $"target framework '{tfm ?? "(none)"}' is below net10.0; the injected " +
            $"reference to {Cs2jArrayProject.ProjectFileName} (net10.0) will fail to " +
            "restore/build with NU1201. Upgrade the host TargetFramework to net10.0+, " +
            "or report the case so Cs2jArray can multi-target.");
    }

    internal static IReadOnlySet<string> ComputeHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph,
        string intermediateRoot)
    {
        // A host is ANY project that compiles a rewritten file. Directory
        // containment alone misses shared sources pulled in via explicit
        // <Compile Include="..\..\src\X.cs" Link="…" /> (e.g. the uri test
        // projects linking Common/ArrayBuilder.cs): the rewritten file lives
        // under the production project's directory, so the test project never
        // got the Cs2jArray reference and failed with CS0103 "Cs2jArray does
        // not exist".
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
            return result;

        var rewrittenFull = rewrittenFiles
            .Select(file => Path.GetFullPath(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file =>
                         file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(
                    projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;

            var compiledSet = ProjectCompileSet.ComputeCompiledSet(
                projectFile, intermediateRoot);
            if (compiledSet != null)
            {
                if (compiledSet.Files.Any(file => rewrittenFull.Contains(file)))
                {
                    result.Add(projectDirectory);
                }

                continue;
            }

            // Unparseable project: fall back to the legacy directory-containment
            // heuristic rather than silently skipping the support reference.
            if (rewrittenFiles.Any(file =>
                    ProjectPreprocessSource.IsSameOrDescendantPath(
                        file, projectDirectory)))
            {
                result.Add(projectDirectory);
            }
        }

        return result;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string propertyName)
        => root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
