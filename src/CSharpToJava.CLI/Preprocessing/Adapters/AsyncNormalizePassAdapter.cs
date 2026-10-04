using CSharpToJava.Core.AsyncNormalize;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// CLI adapter for the C18 async-normalize preprocess pass. Follows the standard
/// spine (no ExecuteAsync/CreateParseOptions override), mirroring
/// <see cref="IteratorLoweringPassAdapter"/>; the residual gate (CS2J1810) runs
/// per file inside the Core preprocessor, so AfterFilesAsync only provides a
/// non-swallowing fallback for the aggregate.
/// </summary>
internal sealed class AsyncNormalizePassAdapter : ProjectPassAdapter
{
    private readonly AsyncNormalizeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly AsyncNormalizePreprocessor _preprocessor = new();
    private readonly List<AsyncNormalizeDiagnostic> _diagnostics = new();
    private readonly AsyncNormalizeStatistics _statistics = new();

    public AsyncNormalizePassAdapter(
        AsyncNormalizeOptions options,
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
        PassCatalog.ImplementedById(PassStubCatalog.Ids.AsyncNormalize);
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
            Console.WriteLine($"[async-normalize] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // The residual gate runs per file inside the Core preprocessor: every
        // residual batch already produced a CS2J1810 diagnostic there (strict
        // mode = Error), and RewriteFileAsync attaches the file path. Do NOT
        // re-emit the same code for the aggregate.
        //
        // Fallback only: the aggregate residual count is positive yet no
        // per-file CS2J1810 was recorded (e.g. the non-strict path keeps
        // counting residuals while suppressing the per-file gate). Surface a
        // single aggregate diagnostic so residuals are never silently
        // swallowed — Error in strict mode, Warning otherwise.
        if (_statistics.ResidualCount > 0
            && !_diagnostics.Any(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1810))
        {
            var severity = _strict
                ? AsyncNormalizeSeverity.Error
                : AsyncNormalizeSeverity.Warning;
            _diagnostics.Add(new AsyncNormalizeDiagnostic(
                severity,
                AsyncNormalizeDiagnosticCode.CS2J1810,
                $"{_statistics.ResidualCount} residual async construct(s) remain after async normalization " +
                "(un-annotated async lambda/local function, ConfigureAwait call(s), or " +
                "un-exempted async void member(s))."));
        }

        // C38 async-stream counterpart of the C18 fallback: the per-file CS2J1849
        // gate fires only in strict mode; the aggregate stream residual bucket is
        // still populated in non-strict mode (skip-tagged sites are whitelisted
        // per file, so this normally stays zero). Surface one aggregate line so
        // residual 'await foreach'/'await using' sites are never swallowed.
        if (_statistics.AsyncStreamResidualCount > 0
            && !_diagnostics.Any(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1849))
        {
            var severity = _strict
                ? AsyncNormalizeSeverity.Error
                : AsyncNormalizeSeverity.Warning;
            _diagnostics.Add(new AsyncNormalizeDiagnostic(
                severity,
                AsyncNormalizeDiagnosticCode.CS2J1849,
                $"{_statistics.AsyncStreamResidualCount} residual async-stream site(s) " +
                "('await foreach' or 'await using') remain after async-stream normalization."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Warning,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet build failed with exit code {exitCode} after async normalization."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet test failed with exit code {exitCode} after async normalization."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == AsyncNormalizeSeverity.Error);

    public override string? BuildChainSummaryLine()
    {
        var lambdasLifted =
            _statistics.AsyncLambdasLiftedZeroCapture
            + _statistics.AsyncLambdasLiftedThisOnly
            + _statistics.AsyncLambdasLiftedVoidWrapper;
        var awaitUsingExpanded =
            _statistics.AwaitUsingStatementsExpanded
            + _statistics.AwaitUsingDeclarationsExpanded;
        // C18 段后追加 C38 分项（0 也输出，保持摘要可 grep 的稳定形态）。
        return $"async-normalize: {lambdasLifted} lambda(s) lifted, " +
               $"{_statistics.WrappersGenerated} wrapper(s), " +
               $"{_statistics.ConfigureAwaitStripped} ConfigureAwait stripped, " +
               $"{_statistics.AwaitSites} await-site(s) kept, " +
               $"{_statistics.SkippedUnsupported} skipped, " +
               $"{_statistics.AwaitForeachExpanded} await-foreach, " +
               $"{awaitUsingExpanded} await-using, " +
               $"{_statistics.AsyncTypeSitesMapped} async-type sites, " +
               $"{_statistics.AsyncIteratorsRejected} async-iterator rejected, " +
               $"{_statistics.ResidualCount} residual";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(AsyncNormalizeStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.ResidualCount += source.ResidualCount;

        _statistics.AsyncMethodsSeen += source.AsyncMethodsSeen;
        _statistics.AwaitSites += source.AwaitSites;
        _statistics.ConfigureAwaitStripped += source.ConfigureAwaitStripped;
        _statistics.AsyncVoidRejected += source.AsyncVoidRejected;
        _statistics.UnsupportedAwaiters += source.UnsupportedAwaiters;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;

        _statistics.AsyncLambdasLiftedZeroCapture += source.AsyncLambdasLiftedZeroCapture;
        _statistics.AsyncLambdasLiftedThisOnly += source.AsyncLambdasLiftedThisOnly;
        _statistics.AsyncLambdasLiftedVoidWrapper += source.AsyncLambdasLiftedVoidWrapper;
        _statistics.AsyncLocalFunctionsLifted += source.AsyncLocalFunctionsLifted;
        _statistics.WrappersGenerated += source.WrappersGenerated;
        _statistics.CapturedAsyncLambdasDeferred += source.CapturedAsyncLambdasDeferred;

        // C38 M2/3a + M3/3b buckets.
        _statistics.AwaitUsingStatementsExpanded += source.AwaitUsingStatementsExpanded;
        _statistics.AwaitUsingDeclarationsExpanded += source.AwaitUsingDeclarationsExpanded;
        _statistics.AsyncDisposeGuardsEmitted += source.AsyncDisposeGuardsEmitted;
        _statistics.AsyncTypeSitesMapped += source.AsyncTypeSitesMapped;
        _statistics.AsyncTypeSitesRejected += source.AsyncTypeSitesRejected;
        _statistics.DuckTypedAsyncDeferred += source.DuckTypedAsyncDeferred;
        _statistics.AwaitForeachExpanded += source.AwaitForeachExpanded;
        _statistics.AsyncEnumeratorsEmitted += source.AsyncEnumeratorsEmitted;
        _statistics.WithCancellationResolved += source.WithCancellationResolved;
        _statistics.EnumerableConfigureAwaitStripped += source.EnumerableConfigureAwaitStripped;
        _statistics.AsyncIteratorsRejected += source.AsyncIteratorsRejected;
        _statistics.AsyncStreamResidualCount += source.AsyncStreamResidualCount;
    }
}
