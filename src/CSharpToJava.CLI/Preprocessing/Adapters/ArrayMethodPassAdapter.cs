using System.Xml.Linq;
using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the static array-helper / multi-dimensional array rewrite pass.
/// Preserves the exact statistics merge, own-release-library exclusion,
/// Cs2jArray injection/project-reference wiring, BOM handling and diagnostic
/// wording of the former ProjectArrayMethodPreprocessor.
/// </summary>
internal sealed class ArrayMethodPassAdapter : ProjectPassAdapter
{
    private readonly ArrayMethodRewriterOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly ArrayMethodRewriter _rewriter = new();
    private readonly List<ArrayMethodRewriterDiagnostic> _diagnostics = new();
    private readonly ArrayMethodRewriterStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();

    public ArrayMethodPassAdapter(
        ArrayMethodRewriterOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ArrayMethod);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree) => new();

    public ArrayMethodRewriterStatistics Statistics => _statistics;
    public IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics => _diagnostics;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories = FindOwnReleaseLibDirectories(tree.SourceRoot);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (IsUnderOwnReleaseLib(context.SourceFilePath, _ownReleaseDirectories))
        {
            if (_verbose)
            {
                Console.WriteLine($"[array-method] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[array-method] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
            var rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.InstanceMemberRewrites > 0 || _statistics.StaticArrayCalls > 0
            || _statistics.ResizeStatements > 0 || _statistics.ForeachFlattened > 0
            || _statistics.RankInlined > 0 || _statistics.MultiDimLengthRewrites > 0)
        {
            await WriteExtensionFilesAsync(tree);
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ArrayMethodRewriterDiagnostic(
            ArrayMethodSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
    {
        // Array-method historically does not run verify builds.
    }

    public override void OnTestsFailed(int exitCode)
    {
        // Array-method historically does not run verify tests.
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ArrayMethodSeverity.Error);

    /// <summary>The convert-project chain printed no array-method summary line.</summary>
    public override string? BuildChainSummaryLine() => null;

    public override string? BuildChainStartLine()
        => _verbose ? "[convert-project] Running array-method preprocessor..." : null;

    public override string? BuildChainVerboseSummaryLine()
        => _verbose
            ? $"[convert-project] array-method: {_statistics.FilesTransformed} transformed, " +
              $"{_statistics.FilesSkippedClean} clean"
            : null;

    public override string? BuildChainFailureBanner()
        => "[convert-project] array-method preprocessing failed; aborting.";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d => $"[array-method] [{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(ArrayMethodRewriterStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.InstanceMemberRewrites += source.InstanceMemberRewrites;
        _statistics.StaticArrayCalls += source.StaticArrayCalls;
        _statistics.ResizeStatements += source.ResizeStatements;
        _statistics.ForeachFlattened += source.ForeachFlattened;
        _statistics.RankInlined += source.RankInlined;
        _statistics.MultiDimLengthRewrites += source.MultiDimLengthRewrites;
    }

    private async Task WriteExtensionFilesAsync(PassTreeContext tree)
    {
        ThrowOnCs2jArrayNameCollision(tree.Layout);

        await ArrayHelperSupportFiles.ReleaseAsync(
            Path.Combine(tree.IntermediateRoot, ArrayHelperSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            tree.Layout, tree.Exclusion, _rewrittenSourceFiles, tree.ProjectGraph);
        EnsureExtensionProjectReferences(
            tree.SourceRoot, tree.IntermediateRoot, tree.Layout, tree.Exclusion,
            hostProjectDirectories, _diagnostics);
    }

    private static void ThrowOnCs2jArrayNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jArrayProject.IsCs2jArrayProject(p) &&
            !IsOwnReleasedArrayProjectFile(p));
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

    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, ArrayHelperSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedArrayProjectFile(
                Path.Combine(dir, Cs2jArrayProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void WarnWhenTargetFrameworkBelowNet10(
        XElement root,
        string projectFilePath,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        var tfm = ProjectUnsignedTypePreprocessor.GetFirstTargetFramework(root);
        if (ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(tfm))
        {
            return;
        }

        diagnostics.Add(new ArrayMethodRewriterDiagnostic(
            ArrayMethodSeverity.Warning,
            $"{projectFilePath}: target framework '{tfm ?? "(none)"}' is below net10.0; the injected " +
            $"reference to {Cs2jArrayProject.ProjectFileName} (net10.0) will fail to restore/build " +
            "with NU1201. Upgrade the host TargetFramework to net10.0+, or report the case so " +
            "Cs2jArray can multi-target."));
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            ArrayHelperSupportFiles.LibDirectoryName,
            Cs2jArrayProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(
                destinationProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

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
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(ArrayHelperSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        WarnWhenTargetFrameworkBelowNet10(root, projectFilePath, diagnostics);

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        if (projectGraph == null || rewrittenFiles.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ComputeProjectsCompilingRewrittenFiles(layout, exclusionInfo, rewrittenFiles);
    }

    private static IReadOnlySet<string> ComputeProjectsCompilingRewrittenFiles(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
        {
            return result;
        }

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, projectDirectory)))
            {
                result.Add(projectDirectory);
            }
        }

        return result;
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
