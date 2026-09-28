using System.Xml.Linq;
using CSharpToJava.Core.DelegatePreprocessor;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the delegate rewrite pass. Preserves the exact statistics merge,
/// prelude support-file injection, project-reference wiring, residual gate and
/// diagnostic wording of the former ProjectDelegatePreprocessor facade.
/// </summary>
internal sealed class DelegatePassAdapter : ProjectPassAdapter
{
    private readonly DelegatePreprocessorOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly DelegatePreprocessor _rewriter = new();
    private readonly List<DelegatePreprocessorDiagnostic> _diagnostics = new();
    private readonly DelegatePreprocessorStatistics _statistics = new();
    private readonly List<string> _rewrittenDestinationFiles = new();

    public DelegatePassAdapter(
        DelegatePreprocessorOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Delegate);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public DelegatePreprocessorStatistics Statistics => _statistics;
    public IReadOnlyList<DelegatePreprocessorDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _rewriter.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[delegate] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenDestinationFiles.Add(context.DestinationFilePath);
            var rewrite = new PassFileRewrite { OutputCode = result.OutputCode, Changed = true };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        // Release support files and inject ProjectReferences
        await PreludeSupportFiles.ReleaseAsync(
            Path.Combine(tree.IntermediateRoot, PreludeSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            tree.Layout, tree.Exclusion, _rewrittenDestinationFiles, tree.ProjectGraph);
        EnsureExtensionProjectReferences(
            tree.SourceRoot, tree.IntermediateRoot, tree.Layout, tree.Exclusion,
            hostProjectDirectories, _diagnostics);

        // Residual detection (syntax-level: anonymous methods only)
        var residualCount = CountResidualAnonymousMethods(tree.IntermediateRoot);
        _statistics.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = _strict
                ? DelegatePreprocessorSeverity.Error
                : DelegatePreprocessorSeverity.Warning;
            _diagnostics.Add(new DelegatePreprocessorDiagnostic(
                severity,
                $"{residualCount} residual anonymous method(s) remaining after preprocessing."));
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new DelegatePreprocessorDiagnostic(
            DelegatePreprocessorSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new DelegatePreprocessorDiagnostic(
            DelegatePreprocessorSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after delegate preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new DelegatePreprocessorDiagnostic(
            DelegatePreprocessorSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after delegate preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == DelegatePreprocessorSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"delegate: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.CombineRewritten} combine, " +
           $"{_statistics.RemoveRewritten} remove, " +
           $"{_statistics.CompoundAssignRewritten} compound, " +
           $"{_statistics.AnonymousMethodRewritten} anonymous method, " +
           $"{_statistics.SkippedUnsupported} skipped, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(DelegatePreprocessorStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.CombineRewritten += source.CombineRewritten;
        _statistics.RemoveRewritten += source.RemoveRewritten;
        _statistics.CompoundAssignRewritten += source.CompoundAssignRewritten;
        _statistics.AnonymousMethodRewritten += source.AnonymousMethodRewritten;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
        _statistics.ResidualCount += source.ResidualCount;
    }

    private static int CountResidualAnonymousMethods(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(content).GetRoot();
            count += root.DescendantNodes().Count(n => n is AnonymousMethodExpressionSyntax);
        }
        return count;
    }

    // ---- EnsureExtensionProjectReferences (mirrors former ProjectDelegatePreprocessor) ----

    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        var ownReleaseDirectories = FindOwnReleaseLibDirectories(layout.SourceRoot);
        var hostDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in rewrittenFiles)
        {
            if (IsUnderOwnReleaseLib(file, ownReleaseDirectories))
                continue;
            var dir = Path.GetDirectoryName(file)!;
            while (dir != null)
            {
                var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
                if (csproj != null)
                {
                    hostDirs.Add(dir);
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }

        if (projectGraph != null)
        {
            foreach (var project in projectGraph.ProjectsInTopologicalOrder)
            {
                if (SupportedProjectFiltering.IsUnderExcludedProject(
                        project.ProjectFilePath, exclusionInfo))
                    continue;
                hostDirs.Add(Path.GetDirectoryName(project.ProjectFilePath)!);
            }
        }

        return hostDirs;
    }

    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, PreludeSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedProjectFile(
                Path.Combine(dir, Cs2jPreludeProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsOwnReleasedProjectFile(string projectFilePath)
    {
        try
        {
            if (!File.Exists(projectFilePath)) return false;
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(PreludeSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            PreludeSupportFiles.LibDirectoryName,
            Cs2jPreludeProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
                continue;

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
        List<DelegatePreprocessorDiagnostic> diagnostics)
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
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            return;

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(PreludeSupportFiles.LibDirectoryName, "**");
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
                continue;

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

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string name)
    {
        var prop = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "PropertyGroup"
                && e.Elements().Any(p => p.Name.LocalName == name));
        return prop?.Element(name)?.Value;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");
}
