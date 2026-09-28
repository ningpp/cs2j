using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.RefOutInPreprocessor;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the ref/out/in boxing pass. Preserves the exact statistics merge,
/// own-release-library exclusion, RefOutIn support-project injection, silent
/// captured verify commands (ROI100/ROI101/ROI102), BOM handling and diagnostic
/// wording of the former ProjectRefOutInPreprocessor.
/// </summary>
internal sealed class RefOutInPassAdapter : ProjectPassAdapter
{
    private readonly RefOutInRewriterOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly RefOutInRewriter _rewriter = new();
    private readonly List<RefOutInRewriterDiagnostic> _diagnostics = new();
    private readonly RefOutInRewriterStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();
    private string? _buildLogSummary;
    private string? _testLogSummary;
    private readonly Dictionary<string, string> _finalOutputs = new(StringComparer.Ordinal);
    private MetadataReference[]? _cachedReferences;

    public RefOutInPassAdapter(
        RefOutInRewriterOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.RefOutIn);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override bool StreamVerifyOutput => false;

    public RefOutInRewriterStatistics Statistics => _statistics;
    public IReadOnlyList<RefOutInRewriterDiagnostic> Diagnostics => _diagnostics;
    public string? BuildLogSummary => _buildLogSummary;
    public string? TestLogSummary => _testLogSummary;

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
                Console.WriteLine($"[ref-out-in] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        if (_cachedReferences == null && context.SemanticModel != null)
            _cachedReferences = context.SemanticModel.Compilation.References.ToArray();

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);
        _finalOutputs[context.DestinationFilePath] =
            result.Changed ? result.OutputCode : context.SourceCode;

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with
            {
                Message = $"{context.RelativePath}: {diagnostic.Message}"
            });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[ref-out-in] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
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
        RunResidualGate(tree);

        // Release support files and inject ProjectReferences
        if (_statistics.RefParamsRewritten > 0 || _statistics.OutParamsRewritten > 0
            || _statistics.RefReturnsRewritten > 0 || _statistics.RefLocalsRewritten > 0
            || _statistics.RefFieldsRewritten > 0 || _statistics.DelegatesRewritten > 0
            || _statistics.CallSitesLifted > 0)
        {
            await WriteExtensionFilesAsync(tree);
        }
    }

    /// <summary>C10/S5: semantic residual gate over the full intermediate tree.</summary>
    private void RunResidualGate(PassTreeContext tree)
    {
        if (_rewrittenSourceFiles.Count == 0 || _cachedReferences == null)
            return;

        var trees = new List<SyntaxTree>();
        foreach (var (path, code) in _finalOutputs)
            trees.Add(CSharpSyntaxTree.ParseText(code, path: path));

        var compilation = CSharpCompilation.Create(
            "refout-residual-verify",
            trees,
            _cachedReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        foreach (var violation in RefOutInPostVerifier.Verify(trees, compilation))
        {
            _diagnostics.Add(new RefOutInRewriterDiagnostic(
                RefOutInDiagnosticCodes.InternalResidue,
                $"{Path.GetFileName(violation.FilePath)}: {violation.Message}",
                RefOutInSeverity.Error));
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
    {
        _diagnostics.Add(new RefOutInRewriterDiagnostic(
            "ROI100",
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            RefOutInSeverity.Error));
        _diagnostics.Add(new RefOutInRewriterDiagnostic(
            RefOutInDiagnosticCodes.RestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            RefOutInSeverity.Error));
    }

    public override void OnBuildFailed(int exitCode)
    {
        // Ref/out/in reports build failures through ReportVerifyOutcome.
    }

    public override void OnTestsFailed(int exitCode)
    {
        // Ref/out/in reports test failures through ReportVerifyOutcome.
    }

    public override void ReportVerifyOutcome(string verb, int exitCode, string capturedOutput)
    {
        var summary = string.IsNullOrEmpty(capturedOutput)
            ? $"EXIT:{exitCode}"
            : (exitCode == 0 ? $"OK:{exitCode}" : $"EXIT:{exitCode}\n{capturedOutput}");

        if (verb == "build")
        {
            _buildLogSummary = exitCode == 0 ? $"OK:{exitCode}" : summary;
            if (exitCode != 0)
            {
                _diagnostics.Add(new RefOutInRewriterDiagnostic(
                    "ROI101",
                    $"dotnet build failed after ref/out/in preprocessing: {_buildLogSummary}",
                    RefOutInSeverity.Error));
                _diagnostics.Add(new RefOutInRewriterDiagnostic(
                    RefOutInDiagnosticCodes.BuildFailed,
                    $"dotnet build failed after ref/out/in preprocessing: {_buildLogSummary}",
                    RefOutInSeverity.Error));
            }
        }
        else
        {
            _testLogSummary = exitCode == 0 ? $"OK:{exitCode}" : summary;
            if (exitCode != 0)
            {
                _diagnostics.Add(new RefOutInRewriterDiagnostic(
                    "ROI102",
                    $"dotnet test failed after ref/out/in preprocessing: {_testLogSummary}",
                    RefOutInSeverity.Error));
                _diagnostics.Add(new RefOutInRewriterDiagnostic(
                    RefOutInDiagnosticCodes.TestsFailed,
                    $"dotnet test failed after ref/out/in preprocessing: {_testLogSummary}",
                    RefOutInSeverity.Error));
            }
        }
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == RefOutInSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"ref-out-in: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.RefParamsRewritten} ref params, " +
           $"{_statistics.OutParamsRewritten} out params, " +
           $"{_statistics.CallSitesLifted} call sites lifted, " +
           $"{_statistics.LockedContractMembers} locked members";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != RefOutInSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(RefOutInRewriterStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.RefParamsRewritten += source.RefParamsRewritten;
        _statistics.OutParamsRewritten += source.OutParamsRewritten;
        _statistics.InByValueParams += source.InByValueParams;
        _statistics.ScopedRemoved += source.ScopedRemoved;
        _statistics.DelegatesRewritten += source.DelegatesRewritten;
        _statistics.RefReturnsRewritten += source.RefReturnsRewritten;
        _statistics.RefLocalsRewritten += source.RefLocalsRewritten;
        _statistics.RefFieldsRewritten += source.RefFieldsRewritten;
        _statistics.ArrayElementProxies += source.ArrayElementProxies;
        _statistics.RefStructParamsRewritten += source.RefStructParamsRewritten;
        _statistics.CallSitesLifted += source.CallSitesLifted;
        _statistics.HoldersInjected += source.HoldersInjected;
        _statistics.AccessorProxies += source.AccessorProxies;
        _statistics.LockedContractMembers += source.LockedContractMembers;
        _statistics.InOverloadKept += source.InOverloadKept;
        _statistics.ExternalRefArgumentsKept += source.ExternalRefArgumentsKept;
    }

    private async Task WriteExtensionFilesAsync(PassTreeContext tree)
    {
        ThrowOnCs2jRefOutInNameCollision(tree.Layout);

        await RefOutInSupportFiles.ReleaseAsync(
            Path.Combine(tree.IntermediateRoot, RefOutInSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeExtensionHostProjectDirectories(
            tree.Layout, tree.Exclusion, _rewrittenSourceFiles);
        EnsureExtensionProjectReferences(
            tree.SourceRoot, tree.IntermediateRoot, tree.Layout, tree.Exclusion,
            hostProjectDirectories, _diagnostics);
    }

    private static void ThrowOnCs2jRefOutInNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jRefOutInProject.IsCs2jRefOutInProject(p) &&
            !IsOwnReleasedRefOutInProjectFile(p));
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jRefOutInProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j ref/out/in support library; rename the project and retry.");
        }
    }

    private static bool IsOwnReleasedRefOutInProjectFile(string projectFilePath)
    {
        try
        {
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(RefOutInSupportFiles.GetProjectFileContent()),
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
            .EnumerateDirectories(sourceRoot, RefOutInSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedRefOutInProjectFile(
                Path.Combine(dir, Cs2jRefOutInProject.ProjectFileName)))
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

    private static IReadOnlySet<string> ComputeExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0) return result;

        foreach (var projectFile in layout.ProjectFiles
                     .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;
            var projectDir = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(f => ProjectPreprocessSource.IsSameOrDescendantPath(f, projectDir)))
                result.Add(projectDir);
        }
        return result;
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            RefOutInSupportFiles.LibDirectoryName,
            Cs2jRefOutInProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;
            var projectDir = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDir))
                continue;
            var destProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(destProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath, string libProjectPath, string intermediateRoot,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath)) return;
        XDocument document;
        try { document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace); }
        catch { return; }
        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            return;

        var projectDir = Path.GetDirectoryName(projectFilePath)!;
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDir, intermediateRoot))
        {
            var removePattern = Path.Combine(RefOutInSupportFiles.LibDirectoryName, "**");
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

        foreach (var refEl in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference").ToList())
        {
            var include = (string?)refEl.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;
            var resolved = Path.GetFullPath(Path.Combine(projectDir, include!));
            if (PathsEqual(resolved, libProjectPath))
                refEl.Remove();
        }

        var relativeInclude = Path.GetRelativePath(projectDir, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));
        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string propertyName) =>
        root.Descendants()
            .Where(e => e.Name.LocalName == propertyName)
            .Select(e => (string?)e.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
