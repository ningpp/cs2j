using System.Xml.Linq;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C47 equality-concat pass. Wraps
/// <see cref="EqualityConcatPreprocessor"/> with the Cs2jPrelude own-release
/// exclusion and the support-library release/project-reference wiring. The
/// support library is released only when at least one delegate == site was
/// actually rewritten (design §2 decision 11): string equality/concat and
/// object/record sites need no support sources.
/// </summary>
internal sealed class EqualityConcatPassAdapter : ProjectPassAdapter
{
    private readonly EqualityConcatOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly EqualityConcatPreprocessor _preprocessor = new();
    private readonly List<EqualityConcatDiagnostic> _diagnostics = new();
    private readonly EqualityConcatStatistics _statistics = new();
    private readonly List<string> _rewrittenDestinationFiles = new();

    public const string IntermediateDirectoryName = ".cs2j-equality-concat-src";

    public EqualityConcatPassAdapter(
        EqualityConcatOptions options,
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

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.EqualityConcat);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public EqualityConcatStatistics Statistics => _statistics;
    public IReadOnlyList<EqualityConcatDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[equality-concat] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenDestinationFiles.Add(context.DestinationFilePath);
            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
            });
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        // Release the prelude only when delegate equality was materialized.
        if (_statistics.DelegateEqualsRewritten == 0)
            return;

        await PreludeSupportFiles.ReleaseAsync(
            Path.Combine(tree.IntermediateRoot, PreludeSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeHostProjectDirectories(
            _rewrittenDestinationFiles, FindOwnReleaseLibDirectories(tree.Layout.SourceRoot));
        EnsureExtensionProjectReferences(
            tree.SourceRoot, tree.IntermediateRoot, tree.Layout, tree.Exclusion,
            hostProjectDirectories, _diagnostics);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new EqualityConcatDiagnostic(
            EqualityConcatSeverity.Warning,
            EqualityConcatDiagnosticCode.CS2J0470,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new EqualityConcatDiagnostic(
            EqualityConcatSeverity.Error,
            EqualityConcatDiagnosticCode.CS2J0470,
            $"dotnet build failed with exit code {exitCode} after equality-concat preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new EqualityConcatDiagnostic(
            EqualityConcatSeverity.Error,
            EqualityConcatDiagnosticCode.CS2J0470,
            $"dotnet test failed with exit code {exitCode} after equality-concat preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == EqualityConcatSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"equality-concat: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.StringEqualsRewritten} string-eq, " +
           $"{_statistics.ObjectReferenceRewritten} object-ref, " +
           $"{_statistics.ConcatChainsFolded} concat chains, " +
           $"{_statistics.ConcatCompoundRewritten} compound, " +
           $"{_statistics.DelegateEqualsRewritten} delegate, " +
           $"{_statistics.RecordEqualsRewritten} record, " +
           $"{_statistics.TemporariesLifted} temps, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d =>
        {
            var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
            return $"[equality-concat] [{d.Severity}] {prefix}{d.Message}";
        }).ToList();

    private void MergeStatistics(EqualityConcatStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.StringEqualsRewritten += source.StringEqualsRewritten;
        _statistics.ObjectReferenceRewritten += source.ObjectReferenceRewritten;
        _statistics.ConcatChainsFolded += source.ConcatChainsFolded;
        _statistics.ConcatOperandsTotal += source.ConcatOperandsTotal;
        _statistics.ConcatCompoundRewritten += source.ConcatCompoundRewritten;
        _statistics.DelegateEqualsRewritten += source.DelegateEqualsRewritten;
        _statistics.RecordEqualsRewritten += source.RecordEqualsRewritten;
        _statistics.TemporariesLifted += source.TemporariesLifted;
        _statistics.ExpressionTreeSitesSkipped += source.ExpressionTreeSitesSkipped;
        _statistics.LiftedEqualityPendingC22 += source.LiftedEqualityPendingC22;
        _statistics.TupleNullEdgeSites += source.TupleNullEdgeSites;
        _statistics.FallbackClassifications += source.FallbackClassifications;
        _statistics.ResidualCount += source.ResidualCount;
        foreach (var pair in source.SkippedByReason)
        {
            _statistics.SkippedByReason.TryGetValue(pair.Key, out var count);
            _statistics.SkippedByReason[pair.Key] = count + pair.Value;
        }
    }

    // ---- Cs2jPrelude project-reference wiring (mirrors DelegatePassAdapter) ----

    /// <summary>
    /// Host projects are exactly the projects containing rewritten delegate
    /// sites (deliberately narrower than the delegate pass, which wires every
    /// graph project).
    /// </summary>
    private static IReadOnlySet<string> ComputeHostProjectDirectories(
        IReadOnlyList<string> rewrittenFiles,
        List<string> ownReleaseDirectories)
    {
        var hostDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in rewrittenFiles)
        {
            if (IsUnderOwnReleaseLib(file, ownReleaseDirectories))
                continue;

            var dir = Path.GetDirectoryName(file)!;
            while (dir != null)
            {
                if (Directory.GetFiles(dir, "*.csproj").FirstOrDefault() != null)
                {
                    hostDirs.Add(dir);
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
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

    private static bool IsUnderOwnReleaseLib(
        string filePath, IReadOnlyList<string> ownReleaseDirectories)
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
        List<EqualityConcatDiagnostic> diagnostics)
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
        List<EqualityConcatDiagnostic> _)
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
                referenceElement.Remove();
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

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
