using CSharpToJava.Core.IndexRange;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C31 index-range (<c>^</c>/<c>..</c>) rewrite pass.
/// Wraps <see cref="IndexRangePreprocessor"/> with the Cs2jArray
/// own-release exclusion, support-library release/project-reference wiring
/// and a whole-intermediate-tree residual rescan.
/// </summary>
internal sealed class IndexRangePassAdapter : ProjectPassAdapter
{
    private readonly IndexRangeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly IndexRangePreprocessor _preprocessor = new();
    private readonly List<IndexRangeDiagnostic> _diagnostics = new();
    private readonly IndexRangeStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();
    private int _expectedRemaining;

    public IndexRangePassAdapter(
        IndexRangeOptions options,
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

    public const string IntermediateDirectoryName = ".cs2j-index-range-src";

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.IndexRange);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public IndexRangeStatistics Statistics => _statistics;
    public IReadOnlyList<IndexRangeDiagnostic> Diagnostics => _diagnostics;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories =
            Cs2jArraySupportLibrary.FindOwnReleaseDirectories(tree.SourceRoot);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function (the shared
    /// <see cref="IndexRangePreprocessor"/> holds no mutable state — each call builds
    /// its own statistics/rewriter and an AdhocWorkspace), so the spine may run the
    /// read + rewrite step in parallel. Every accumulation (statistics, diagnostics,
    /// the expected-residual counter, the rewritten-file list, verbose lines) is
    /// deferred to <see cref="MergeFilePureResult"/>, which the runner calls serially
    /// in source-file order — keeping summary counts, diagnostic order and the
    /// residual-gate baseline identical to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    /// <summary>Per-file side effects carried out of the parallel phase.</summary>
    private sealed record IndexRangeFileState(IndexRangeResult Result, int ExpectedRemainingContribution);

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        if (Cs2jArraySupportLibrary.IsUnderOwnRelease(
                context.SourceFilePath, _ownReleaseDirectories))
        {
            // Support-library code is never rewritten; its verbose line is deferred
            // to the serial merge so console order stays identical.
            return Task.FromResult(new PassFilePureResult
            {
                Context = context,
                Rewrite = null,
                PassState = null,
            });
        }

        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel!, _options);
        PassFileRewrite? rewrite = result.Changed
            ? new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
            }
            : null;

        // Dirty but diagnostics-only: those ^/.. nodes legitimately remain. Counted
        // in the parallel phase (pure function of the source) and applied serially.
        var contribution = result.Changed
            ? 0
            : CountIndexRangeExpressions(context.SourceCode);

        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = new IndexRangeFileState(result, contribution),
        });
    }

    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        if (pure.PassState is null)
        {
            if (_verbose)
            {
                Console.WriteLine(
                    $"[index-range] clean (own release copy): {context.RelativePath}");
            }

            return;
        }

        var state = (IndexRangeFileState)pure.PassState;
        var result = state.Result;
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        _expectedRemaining += state.ExpectedRemainingContribution;

        if (_verbose)
        {
            Console.WriteLine(
                $"[index-range] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
        }
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_rewrittenSourceFiles.Count > 0)
        {
            await Cs2jArraySupportLibrary.EnsureSupportAsync(
                tree, _rewrittenSourceFiles, AddTfmWarning);
        }

        var remaining = CountIndexRangeExpressionsInTree(tree.IntermediateRoot);
        _statistics.ResidualCount = Math.Max(0, remaining - _expectedRemaining);

        if (remaining > _expectedRemaining)
        {
            var severity = _strict
                ? IndexRangeSeverity.Error
                : IndexRangeSeverity.Warning;
            _diagnostics.Add(new IndexRangeDiagnostic(
                "IR140",
                severity,
                $"{remaining - _expectedRemaining} residual ^/.. expression(s) " +
                "remaining in the intermediate tree."));
        }
    }

    private void AddTfmWarning(string projectFilePath, string message)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR201",
            IndexRangeSeverity.Warning,
            $"{projectFilePath}: {message}"));

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR201",
            IndexRangeSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR140",
            IndexRangeSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after index-range preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR140",
            IndexRangeSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after index-range preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == IndexRangeSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"index-range: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.HatElementSites} hat sites, " +
           $"{_statistics.RangeSliceSites} slice sites, " +
           $"{_statistics.ReceiverTempsLifted + _statistics.BoundTempsLifted} temps, " +
           $"{_statistics.StandaloneIndexSites + _statistics.StandaloneRangeSites} standalone, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d =>
        {
            var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
            return $"[index-range] [{d.Severity}] {prefix}{d.Message}";
        }).ToList();

    private static int CountIndexRangeExpressions(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return CountIndexRangeExpressions(root);
    }

    private static int CountIndexRangeExpressions(SyntaxNode root)
        => root.DescendantNodesAndSelf()
            .Count(n =>
                n.RawKind == (int)SyntaxKind.IndexExpression
                || n.RawKind == (int)SyntaxKind.RangeExpression);

    private static int CountIndexRangeExpressionsInTree(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(csFile)).GetRoot();
            count += CountIndexRangeExpressions(root);
        }

        return count;
    }

    private void MergeStatistics(IndexRangeStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.HatElementSites += source.HatElementSites;
        _statistics.RangeSliceSites += source.RangeSliceSites;
        _statistics.RangeInvocationSites += source.RangeInvocationSites;
        _statistics.StandaloneIndexSites += source.StandaloneIndexSites;
        _statistics.StandaloneRangeSites += source.StandaloneRangeSites;
        _statistics.ReceiverTempsLifted += source.ReceiverTempsLifted;
        _statistics.BoundTempsLifted += source.BoundTempsLifted;
        _statistics.LengthTempReads += source.LengthTempReads;
        _statistics.ArrayHelperCalls += source.ArrayHelperCalls;
        _statistics.StringSpecializations += source.StringSpecializations;
        foreach (var pair in source.SkippedByReason)
        {
            _statistics.SkippedByReason.TryGetValue(pair.Key, out var count);
            _statistics.SkippedByReason[pair.Key] = count + pair.Value;
        }
    }
}
