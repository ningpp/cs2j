using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closure-lift pass.
/// M1 (this task): deliberately NOT wired into PassChainFactory — the registry
/// flip is Task 16. The descriptor is file-private so the Stub entry in
/// PassStubCatalog stays the single registry truth until then; ProjectPassRunner
/// never consults PassStatus (verified), so the SDK harness / tests can already
/// drive this adapter directly. M2 processing performs the real holder-lift
/// rewrite (Changed files return PassFileRewrite); AuditOnly keeps every file
/// verbatim with CS2J3207 rows, and AfterFilesAsync runs the residual gate.
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
        description: "M2 holder-lift: rewrite mutably captured locals/parameters into private holder classes before lambdas (C32).",
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
            string status;
            if (_options.AuditOnly)
                status = "audit";
            else if (result.Changed)
                status = "transformed";
            else if (result.Diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
                status = "blocked (copy)";
            else
                status = "clean (copy)";
            Console.WriteLine($"[closure-lift] {status}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Design §7.3 gate: re-parse every .cs under the intermediate tree in
        // audit terms. Transformed files are 0 by construction; frozen files
        // (any CS2J3201-3206 row) keep their original captures and are owned
        // by their per-file rows — M3 gate §8.4 excludes such owned Warning
        // rows when judging the tree.
        var residual = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var fileCount = _preprocessor.CountResidualMutableCaptures(File.ReadAllText(csFile));
            if (fileCount > 0 && _verbose)
            {
                Console.WriteLine(
                    $"[closure-lift] residual gate: {fileCount} capture(s) in {Path.GetRelativePath(tree.IntermediateRoot, csFile)}");
            }
            residual += fileCount;
        }

        // Measured in the intermediate tree (same '=' discipline as E02's
        // LocalFunctionsResidual), never added on top of per-file statistics.
        _statistics.ResidualMutableCaptures = residual;

        if (residual > 0)
        {
            var severity = _strict
                ? ClosureLiftSeverity.Error
                : ClosureLiftSeverity.Warning;
            _diagnostics.Add(new ClosureLiftDiagnostic(
                severity,
                ClosureLiftCodes.UnresolvableSymbol,
                $"C32 residual gate: {residual} mutable lambda capture(s) remain in the intermediate tree; " +
                "the complete capture-site set cannot be guaranteed for those file(s)."));
        }

        return Task.CompletedTask;
    }

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
        var blocked = _statistics.SkippedByReason.Values.Sum();
        return $"closure-lift: {_statistics.FilesTransformed} transformed, " +
               $"{_statistics.FilesSkippedClean} clean, " +
               $"{_statistics.HolderClassesGenerated} holder class(es), " +
               $"{_statistics.CapturesLifted} capture(s) lifted, " +
               $"{blocked} blocked, " +
               $"{_statistics.ResidualMutableCaptures} residual";
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
