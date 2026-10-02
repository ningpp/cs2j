using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closure-lift pass.
/// M1 (this task): deliberately NOT wired into PassChainFactory — the registry
/// flip is Task 16. The descriptor is file-private so the Stub entry in
/// PassStubCatalog stays the single registry truth until then; ProjectPassRunner
/// never consults PassStatus (verified), so the SDK harness / tests can already
/// drive this adapter directly. M1 processing is audit-only: every file is
/// copied verbatim and CS2J3207 rows land in Diagnostics.
/// </summary>
internal sealed class ClosureLiftPassAdapter : ProjectPassAdapter
{
    private static readonly PassDescriptor M1Descriptor = new(
        PassStubCatalog.Ids.ClosureLift,
        "Closure/display class lift",
        ".cs2j-closure-lift-src",
        PassStage.StateMachine,
        PassStatus.Implemented,
        defaultEnabled: false,
        runAfterIds: new[] { PassIds.RefOutIn },
        verifyBuildByDefault: true,
        verifyTestsByDefault: true,
        description: "M1 audit-only: report mutable lambda captures and prospective holder names; no rewrite yet.",
        catalogItems: "C32");

    private readonly ClosureLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly ClosureLiftPreprocessor _preprocessor = new();
    private readonly List<ClosureLiftDiagnostic> _diagnostics = new();
    private readonly ClosureLiftStatistics _statistics = new();

    public ClosureLiftPassAdapter(
        ClosureLiftOptions options,
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

    public override PassDescriptor Descriptor => M1Descriptor;
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ClosureLiftStatistics Statistics => _statistics;
    public IReadOnlyList<ClosureLiftDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
            _diagnostics.Add(diagnostic.WithFilePath(context.RelativePath));

        if (_verbose)
        {
            var auditRows = result.Diagnostics.Count(d => d.Code == ClosureLiftCodes.AuditRow);
            Console.WriteLine(
                $"[closure-lift] audit: {auditRows} candidate capture(s): {context.RelativePath}");
        }

        // M1: always verbatim. Task 12 returns a PassFileRewrite once rewriting exists.
        return Task.FromResult<PassFileRewrite?>(null);
    }

    // M1: the tree is a verbatim copy, so there is no residual gate yet.
    // Task 15 adds re-parse + residual mutable-capture counting here.
    public override Task AfterFilesAsync(PassTreeContext tree) => Task.CompletedTask;

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Warning,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet build failed with exit code {exitCode} during closure-lift audit."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ClosureLiftDiagnostic(
            ClosureLiftSeverity.Error,
            ClosureLiftCodes.UnsupportedBoundary,
            $"dotnet test failed with exit code {exitCode} during closure-lift audit."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ClosureLiftSeverity.Error);

    public override string? BuildChainSummaryLine()
    {
        var candidates = _statistics.CapturesByReason.Values.Sum();
        var blocked = _statistics.SkippedByReason.Values.Sum();
        // M1 wording; Task 15 switches to the transform/holders/residual line.
        return $"closure-lift (audit): {_statistics.FilesScanned} scanned, " +
               $"{candidates} mutable captures reported, {blocked} blocked, " +
               $"{_statistics.ForeachPerIterationGroups} foreach group(s)";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != ClosureLiftSeverity.Info)
            .Select(d => $"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}")
            .ToList();

    private void MergeStatistics(ClosureLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.HolderClassesGenerated += source.HolderClassesGenerated;
        _statistics.HolderInstancesGenerated += source.HolderInstancesGenerated;
        _statistics.CapturesLifted += source.CapturesLifted;
        _statistics.SitesRewritten += source.SitesRewritten;
        _statistics.ForeachPerIterationGroups += source.ForeachPerIterationGroups;
        _statistics.ResidualMutableCaptures += source.ResidualMutableCaptures;

        foreach (var kvp in source.CapturesByReason)
        {
            if (!_statistics.CapturesByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.CapturesByReason[kvp.Key] += kvp.Value;
        }

        foreach (var kvp in source.SkippedByReason)
        {
            if (!_statistics.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
                _statistics.SkippedByReason[kvp.Key] += kvp.Value;
        }
    }
}
