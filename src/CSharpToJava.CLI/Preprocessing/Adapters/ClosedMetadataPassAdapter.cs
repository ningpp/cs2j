using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closed-metadata (C30) pass. M2' skeleton: the standard spine
/// runs (whole-tree analyze once, per-file rewrite), but the registration plan is
/// empty so every file stays a clean byte-level copy, no files are generated and
/// the residual scan reports zero findings (M3/M4 deliverables).
/// </summary>
internal sealed class ClosedMetadataPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-closed-metadata-src";

    private readonly ClosedMetadataOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly ClosedMetadataPreprocessor _preprocessor = new();
    private readonly List<ClosedMetadataDiagnostic> _diagnostics = new();
    private readonly ClosedMetadataStatistics _statistics = new();

    public ClosedMetadataPassAdapter(
        ClosedMetadataOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ClosedMetadata);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override string CompilationName => "cs2j-closed-metadata-preprocess";

    public ClosedMetadataStatistics Statistics => _statistics;
    public IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        // Chain hard dependency (design §4.4): registration must read the final
        // post-C02 symbol shapes, so generic-deepen has to be enabled in the
        // same run — otherwise fail loudly with CS2J1205 instead of silently
        // registering against intermediate shapes.
        if (_options.EnabledPassIds is not { } enabledIds || !enabledIds.Contains(PassIds.GenericDeepen))
        {
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Error,
                ClosedMetadataRule.ChainPosition,
                ClosedMetadataCodes.GenericDeepenRequired,
                "closed-metadata requires generic-deepen to be enabled in the same run " +
                "(hard chain dependency: registration reads the final post-generic-deepen " +
                "symbol shapes); enable both passes or disable closed-metadata."));
            return Task.CompletedTask;
        }

        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_options);
        if (_verbose)
        {
            Console.WriteLine("[closed-metadata] analysis skeleton: 0 closed types registered (M2')");
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // M2' skeleton: zero rewrites — the runner performs the clean byte-level
        // copy for every file (BOM and line endings preserved verbatim).
        if (_verbose)
        {
            Console.WriteLine($"[closed-metadata] clean (copy): {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // M2' skeleton: no generated registration files to distribute and no
        // residual findings yet — both land with the M3/M4 wiring (design §5.3).
        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Warning,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Error,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after closed-metadata preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Error,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after closed-metadata preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ClosedMetadataSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ClosedMetadataSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
