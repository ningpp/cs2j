using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the default-parameter strip pass (former ProjectDefaultParameterStripper).
/// Two-phase: Collect() across the whole compilation before per-file Strip(), with
/// DefineConstants harvested from every included .csproj.
/// </summary>
internal sealed class StripDefaultPassAdapter : ProjectPassAdapter
{
    private readonly DefaultParameterStripperOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly DefaultParameterStripper _preprocessor = new();
    private readonly List<DefaultParameterStripDiagnostic> _diagnostics = new();
    private readonly DefaultParameterStripperStatistics _statistics = new();
    private CSharpParseOptions? _parseOptions;

    public StripDefaultPassAdapter(
        DefaultParameterStripperOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.StripDefault);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public DefaultParameterStripperStatistics Statistics => _statistics;
    public IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics => _diagnostics;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
    {
        if (_parseOptions != null)
        {
            return _parseOptions;
        }

        var symbols = CollectPreprocessorSymbols(tree.Layout, tree.Exclusion);
        symbols.Add("NETCOREAPP");
        _parseOptions = new CSharpParseOptions(preprocessorSymbols: symbols.ToArray());
        return _parseOptions;
    }

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        // Pass 1: collect existing signatures for cross-file conflict detection.
        foreach (var tree in compilation.SyntaxTrees)
        {
            // Synthetic trees (none for this pass today) never have backing sources.
            if (!File.Exists(tree.FilePath))
            {
                continue;
            }

            _preprocessor.Collect(tree, compilation.GetSemanticModel(tree));
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Strip(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        // Pass 2 diagnostics.
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic);
        }

        PassFileRewrite? rewrite = null;
        if (result.Changed)
        {
            rewrite = new PassFileRewrite { OutputCode = result.OutputCode, Changed = true };

            // Pass 3: verify no residual defaults remain.
            foreach (var error in DefaultParameterVerifier.Verify(result.OutputCode))
            {
                _diagnostics.Add(new DefaultParameterStripDiagnostic(
                    error.Severity,
                    $"{context.RelativePath}: {error.Message}"));
            }
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[strip-default] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(rewrite);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new DefaultParameterStripDiagnostic(
            DefaultParameterStripSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new DefaultParameterStripDiagnostic(
            DefaultParameterStripSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after strip-default preprocessing. " +
            "The strip-default rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new DefaultParameterStripDiagnostic(
            DefaultParameterStripSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after strip-default preprocessing."));

    public override void ReportVerifyCommandResult(string verb, string targetPath, int exitCode)
    {
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"dotnet {verb} exited with code {exitCode} for {targetPath}");
        }
        else
        {
            Console.WriteLine($"[strip-default] dotnet {verb} succeeded for {targetPath}");
        }
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == DefaultParameterStripSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"strip-default: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.DeclarationsStripped} declarations stripped, " +
           $"{_statistics.OverloadsGenerated} overloads generated";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d => $"[{d.Severity}] {d.Message}").ToList();

    /// <summary>
    /// Collects DefineConstants from every included .csproj so call sites inside
    /// #if FEATURE_XYZ blocks are visible to the rewriter.
    /// </summary>
    private static HashSet<string> CollectPreprocessorSymbols(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles)
        {
            if (!projectFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(projectFile);
                var doc = System.Xml.Linq.XDocument.Load(new StringReader(content));
                if (doc.Root == null)
                {
                    continue;
                }

                foreach (var element in doc.Root.Descendants())
                {
                    if (!string.Equals(element.Name.LocalName, "DefineConstants", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = element.Value;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = part.Trim();
                        if (trimmed.StartsWith("$("))
                        {
                            continue; // skip MSBuild variable references
                        }

                        symbols.Add(trimmed);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore unreadable project files; historical behavior.
            }
        }

        return symbols;
    }

    private void MergeStatistics(DefaultParameterStripperStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.DeclarationsStripped += source.DeclarationsStripped;
        _statistics.ParametersStripped += source.ParametersStripped;
        _statistics.OverloadsGenerated += source.OverloadsGenerated;
        _statistics.CallSitesRewritten += source.CallSitesRewritten;
        _statistics.SkippedNoBody += source.SkippedNoBody;
        _statistics.SkippedConflict += source.SkippedConflict;
        _statistics.SkippedHidesAbstract += source.SkippedHidesAbstract;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
    }
}
