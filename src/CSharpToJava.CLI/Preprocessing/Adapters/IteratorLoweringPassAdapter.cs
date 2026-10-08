using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// CLI adapter for the iterator-lowering preprocess pass. Follows the standard
/// spine (no ExecuteAsync override), with a residual gate in AfterFilesAsync.
/// </summary>
internal sealed class IteratorLoweringPassAdapter : ProjectPassAdapter
{
    private readonly IteratorLoweringOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly IteratorLoweringPreprocessor _preprocessor = new();
    private readonly List<IteratorLoweringDiagnostic> _diagnostics = new();
    private readonly IteratorLoweringStatistics _statistics = new();

    public IteratorLoweringPassAdapter(
        IteratorLoweringOptions options,
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
        PassCatalog.ImplementedById(PassStubCatalog.Ids.IteratorLowering);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[iterator-lowering] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.ResidualCount > 0)
        {
            var severity = _strict
                ? IteratorLoweringSeverity.Error
                : IteratorLoweringSeverity.Warning;
            _diagnostics.Add(new IteratorLoweringDiagnostic(
                severity,
                IteratorLoweringDiagnosticCode.CS2J0402,
                $"{_statistics.ResidualCount} residual yield statement(s) remaining after preprocessing."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Warning,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet build failed with exit code {exitCode} after iterator lowering."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            IteratorLoweringDiagnosticCode.CS2J0420,
            $"dotnet test failed with exit code {exitCode} after iterator lowering."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == IteratorLoweringSeverity.Error);

    public override string? BuildChainSummaryLine()
        => $"iterator-lowering: {_statistics.IteratorMembersLowered} member(s) lowered, " +
           $"{_statistics.YieldReturnSites} yield-return, " +
           $"{_statistics.YieldBreakSites} yield-break, " +
           $"{_statistics.HoistedParameters + _statistics.HoistedLocals} hoisted, " +
           $"{_statistics.HoistedEnumerators} enumerator(s), " +
           $"{_statistics.FinallyRegions} protected region(s), " +
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

    private void MergeStatistics(IteratorLoweringStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.IteratorMembersLowered += source.IteratorMembersLowered;
        _statistics.StateMachinesGenerated += source.StateMachinesGenerated;
        _statistics.YieldReturnSites += source.YieldReturnSites;
        _statistics.YieldBreakSites += source.YieldBreakSites;
        _statistics.HoistedParameters += source.HoistedParameters;
        _statistics.HoistedLocals += source.HoistedLocals;
        _statistics.HoistedEnumerators += source.HoistedEnumerators;
        _statistics.FinallyRegions += source.FinallyRegions;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
        _statistics.ResidualCount += source.ResidualCount;
    }
}
