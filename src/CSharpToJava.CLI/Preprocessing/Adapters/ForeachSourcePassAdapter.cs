using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the foreach-source pass (C16). Runs on the standard spine:
/// FS-1 wraps plain foreach over string with ToCharArray() (with the
/// ruling-6 explicit element conversion form) and M2 closes residual
/// deconstruction forms. No support library; verify build/tests both default
/// on via the descriptor.
/// </summary>
internal sealed class ForeachSourcePassAdapter : ProjectPassAdapter
{
    private readonly ForeachSourceOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly ForeachSourcePreprocessor _preprocessor = new();
    private readonly List<ForeachSourceDiagnostic> _diagnostics = new();
    private readonly ForeachSourceStatistics _statistics = new();

    public ForeachSourcePassAdapter(
        ForeachSourceOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool strict)
    {
        _options = new ForeachSourceOptions
        {
            Strict = strict,
            ReportInfo = options.ReportInfo,
        };
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor
        => PassCatalog.ImplementedById(PassStubCatalog.Ids.ForeachSource);

    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    // Same parse options as foreach-deconstruction (no NETCOREAPP symbol).
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new();

    public override bool DeleteBuildOutputsAfterRestore => true;

    public ForeachSourceStatistics Statistics => _statistics;
    public IReadOnlyList<ForeachSourceDiagnostic> Diagnostics => _diagnostics;

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
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[foreach-source] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // FS-5 residual gates. Per-file residual counters come straight from
        // the rewriter output bookkeeping — the intermediate tree is never
        // parsed a second time here (the spine's verify build is the semantic
        // safety net). ref/await/KV/lowered-deconstruct sites never enter the
        // failing buckets. The C16 gate (CS2J5440, string/deconstruction) and
        // the C40 gate (CS2J5470, pattern enumerator escape bucket) are
        // counted and reported separately.
        var residualFailures = _statistics.ResidualStringSites
            + _statistics.ResidualDeconstructionSites;
        if (residualFailures > 0)
        {
            var severity = _options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            _diagnostics.Add(new ForeachSourceDiagnostic(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceResidualGate,
                $"{residualFailures} residual foreach site(s) remaining after foreach-source " +
                $"({_statistics.ResidualStringSites} bare string, " +
                $"{_statistics.ResidualDeconstructionSites} tuple/other deconstruction)."));
        }

        if (_statistics.ResidualEnumeratorSites > 0)
        {
            var severity = _options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            _diagnostics.Add(new ForeachSourceDiagnostic(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceResidualEnumeratorGate,
                $"{_statistics.ResidualEnumeratorSites} residual pattern-enumerator foreach site(s) " +
                "remaining after foreach-source (non-array/non-string/non-IEnumerable source with a valid " +
                "enumerator binding that no rule lowered)."));
        }

        // M3 FS-5 ref bucket (CS2J5495): defensive only — every unbindable
        // ref site is normally terminated inside the pass with CS2J5485, and
        // that terminal already keeps the node in the tree; count here only
        // ref sites that escaped BOTH an FR rewrite and a terminal diagnostic.
        if (_statistics.ResidualRefSites > 0)
        {
            var severity = _options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            _diagnostics.Add(new ForeachSourceDiagnostic(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceResidualRefGate,
                $"{_statistics.ResidualRefSites} ref/ref-readonly foreach site(s) escaped the FR rules " +
                "without a CS2J5485 terminal; the nodes survive unlowered."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ForeachSourceDiagnostic(
            ForeachSourceSeverity.Error,
            PreprocessDiagnosticCodes.ForeachSourceRestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ForeachSourceDiagnostic(
            ForeachSourceSeverity.Error,
            PreprocessDiagnosticCodes.ForeachSourceBuildFailed,
            $"dotnet build failed with exit code {exitCode} after foreach-source preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ForeachSourceDiagnostic(
            ForeachSourceSeverity.Error,
            PreprocessDiagnosticCodes.ForeachSourceTestsFailed,
            $"dotnet test failed with exit code {exitCode} after foreach-source preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ForeachSourceSeverity.Error);

    public override string BuildChainSummaryLine()
    {
        var errorCount = _diagnostics.Count(d => d.Severity == ForeachSourceSeverity.Error);
        // C40 enumerator adapter segment follows the C16 counters.
        return $"foreach-source: {_statistics.FilesTransformed} transformed, " +
               $"{_statistics.FilesSkippedClean} clean, " +
               $"{_statistics.StringForeachWrapped} string site(s) wrapped, " +
               $"{_statistics.TupleResidualSeen} tuple residual(s), " +
               $"{_statistics.OtherResidualDiagnosed + _statistics.KvResidualSeen} other/KV residual(s), " +
               $"{_statistics.DeconstructResidualLowered} deconstruct(s) lowered, " +
               $"{_statistics.DuckEnumeratorSitesLowered} duck enumerator(s) lowered " +
               $"(ext {_statistics.ExtensionGetEnumeratorSites}, direct {_statistics.DirectEnumeratorSites}, " +
               $"struct {_statistics.StructEnumeratorSites}, dispose {_statistics.DisposePatternCallSites} " +
               $"[boxed {_statistics.DisposeBoxedStructSites}], no-dispose {_statistics.NoDisposeSites}, " +
               $"dynamic {_statistics.DynamicForeachSites}), " +
               $"{_statistics.SpanIndexLoopSites} span index loop(s), " +
               $"{_statistics.RefIndexLoopSites} ref index loop(s), " +
               $"{_statistics.RefReadonlySites} ref-readonly index loop(s), " +
               $"{_statistics.RefCurrentEnumeratorSites} ref-Current enumerator(s) " +
               $"[5485 {_statistics.ErrorsByCode.GetValueOrDefault(PreprocessDiagnosticCodes.ForeachSourceRefForeachUnsupported)}, " +
               $"5486 {_statistics.ErrorsByCode.GetValueOrDefault(PreprocessDiagnosticCodes.ForeachSourceRefIterationCapture)}, " +
               $"residual ref {_statistics.ResidualRefSites}], " +
               $"{_statistics.UnresolvedForeachSites} unresolved site(s), " +
               $"{_statistics.ResidualEnumeratorSites} residual enumerator site(s), " +
               $"{errorCount} error(s)";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => verbose || d.Severity != ForeachSourceSeverity.Info)
            .Select(d =>
            {
                // MSBuild-style: path(line,column): severity code: message
                var location = d.Line is > 0
                    ? $"{d.FilePath}({d.Line},{d.Column ?? 1}): "
                    : d.FilePath != null ? $"{d.FilePath}: " : string.Empty;
                var level = d.Severity.ToString().ToLowerInvariant();
                return $"{location}{level} {d.Code}: {d.Message}";
            })
            .ToList();

    private void MergeStatistics(ForeachSourceStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.StringForeachWrapped += source.StringForeachWrapped;
        _statistics.TupleResidualSeen += source.TupleResidualSeen;
        _statistics.OtherResidualDiagnosed += source.OtherResidualDiagnosed;
        _statistics.KvResidualSeen += source.KvResidualSeen;
        _statistics.DeconstructResidualLowered += source.DeconstructResidualLowered;
        _statistics.RefForeachSeen += source.RefForeachSeen;
        _statistics.AwaitForeachSeen += source.AwaitForeachSeen;
        _statistics.SitesPassedThrough += source.SitesPassedThrough;
        _statistics.ResidualStringSites += source.ResidualStringSites;
        _statistics.ResidualDeconstructionSites += source.ResidualDeconstructionSites;

        // C40 enumerator adapter buckets (design §5.3).
        _statistics.DuckEnumeratorSitesLowered += source.DuckEnumeratorSitesLowered;
        _statistics.ExtensionGetEnumeratorSites += source.ExtensionGetEnumeratorSites;
        _statistics.DirectEnumeratorSites += source.DirectEnumeratorSites;
        _statistics.StructEnumeratorSites += source.StructEnumeratorSites;
        _statistics.DisposePatternCallSites += source.DisposePatternCallSites;
        _statistics.DisposeBoxedStructSites += source.DisposeBoxedStructSites;
        _statistics.NoDisposeSites += source.NoDisposeSites;
        _statistics.SpanIndexLoopSites += source.SpanIndexLoopSites;
        _statistics.RefIndexLoopSites += source.RefIndexLoopSites;
        _statistics.RefReadonlySites += source.RefReadonlySites;
        _statistics.RefCurrentEnumeratorSites += source.RefCurrentEnumeratorSites;
        _statistics.DynamicForeachSites += source.DynamicForeachSites;
        _statistics.UnresolvedForeachSites += source.UnresolvedForeachSites;
        _statistics.ResidualEnumeratorSites += source.ResidualEnumeratorSites;
        _statistics.ResidualRefSites += source.ResidualRefSites;

        foreach (var pair in source.ErrorsByCode)
        {
            _statistics.ErrorsByCode[pair.Key] =
                _statistics.ErrorsByCode.GetValueOrDefault(pair.Key) + pair.Value;
        }
    }
}
