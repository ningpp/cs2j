using CSharpToJava.Core.NullCoalesceRewriter;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>Adapter for the ??/??= rewrite pass (former ProjectNullCoalescePreprocessor).</summary>
internal sealed class NullCoalescePassAdapter : ProjectPassAdapter
{
    private readonly NullCoalesceOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly NullCoalesceRewriter _rewriter = new();
    private readonly List<NullCoalesceDiagnostic> _diagnostics = new();
    private readonly NullCoalesceStatistics _statistics = new();

    public NullCoalescePassAdapter(
        NullCoalesceOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.NullCoalesce);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public NullCoalesceStatistics Statistics => _statistics;
    public IReadOnlyList<NullCoalesceDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function (the shared
    /// <see cref="NullCoalesceRewriter"/> holds no mutable state — each call builds
    /// its own statistics/rewriter and an AdhocWorkspace), so the spine may run the
    /// read + rewrite step in parallel. Every accumulation is deferred to
    /// <see cref="MergeFilePureResult"/>, which the runner calls serially in
    /// source-file order — keeping the summary counts and diagnostic order identical
    /// to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        var result = _rewriter.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        PassFileRewrite? rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = result,
        });
    }

    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var result = (NullCoalesceResult)pure.PassState!;
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[null-coalesce] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Historical behavior: the per-file residual accumulator is replaced by a
        // fresh scan of the written intermediate tree. W-06 (upgrade plan
        // §4.3/G-18, sequential-failure policy): residual sites carrying a
        // collection expression are OWNED downstream — expression-desugar (C36)
        // lowers the collection syntax and the converter's coalesce path renders
        // the operator — so they are deferred with a Warning (file/line list)
        // instead of aborting the chain as a strict Error before the later
        // passes (and the generic passes) ever run. The classifier is the SAME
        // single implementation the per-file post-check uses.
        var deferredSites = new List<(string File, int Line)>();
        var strictResidual = 0;
        var residualTotal = 0;

        foreach (var csFile in Directory.EnumerateFiles(
                     tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(csFile)).GetRoot();
            foreach (var node in root.DescendantNodes().Where(n =>
                         n is BinaryExpressionSyntax b
                             && b.IsKind(SyntaxKind.CoalesceExpression)
                         || n is AssignmentExpressionSyntax a
                             && a.IsKind(SyntaxKind.CoalesceAssignmentExpression)))
            {
                residualTotal++;
                if (NullCoalesceRewriter.IsOwnedDownstream(node))
                {
                    int line;
                    try
                    {
                        line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    }
                    catch
                    {
                        line = 0;
                    }

                    deferredSites.Add((
                        Path.GetRelativePath(tree.SourceRoot, csFile), line));
                }
                else
                {
                    strictResidual++;
                }
            }
        }

        _statistics.ResidualCount = residualTotal;
        _statistics.DeferredDownstreamSites = deferredSites.Count;

        if (deferredSites.Count > 0)
        {
            var locationList = string.Join(
                ", ",
                deferredSites.Take(5).Select(site => $"{site.File}:{site.Line}"));
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"{deferredSites.Count} ??/??= site(s) deferred to later passes (the collection " +
                "expression is owned by expression-desugar; the converter's coalesce path renders " +
                $"the operator): {locationList}{(deferredSites.Count > 5 ? " …" : "")}."));
        }

        if (strictResidual > 0)
        {
            var severity = _strict
                ? NullCoalesceSeverity.Error
                : NullCoalesceSeverity.Warning;
            _diagnostics.Add(new NullCoalesceDiagnostic(
                severity,
                $"{strictResidual} residual ??/??= operator(s) remaining after preprocessing."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new NullCoalesceDiagnostic(
            NullCoalesceSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new NullCoalesceDiagnostic(
            NullCoalesceSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after null-coalesce preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new NullCoalesceDiagnostic(
            NullCoalesceSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after null-coalesce preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == NullCoalesceSeverity.Error);

    public override string BuildChainSummaryLine()
    {
        var line = $"null-coalesce: {_statistics.FilesTransformed} transformed, " +
                   $"{_statistics.FilesSkippedClean} clean, " +
                   $"{_statistics.NullCoalescingRewritten} ?? rewritten, " +
                   $"{_statistics.NullCoalescingAssignRewritten} ??= rewritten, " +
                   $"{_statistics.TempVariablesGenerated} temp vars, " +
                   $"{_statistics.ExpressionBodiesExpanded} arrow bodies expanded, " +
                   $"{_statistics.ResidualCount} residual";

        // W-06: the deferred segment appears only when a deferral happened,
        // keeping no-deferral runs byte-identical.
        if (_statistics.DeferredDownstreamSites > 0)
            line += $", {_statistics.DeferredDownstreamSites} deferred-downstream";

        return line;
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d =>
        {
            var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
            return $"[{d.Severity}] {prefix}{d.Message}";
        }).ToList();

    private void MergeStatistics(NullCoalesceStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.NullCoalescingRewritten += source.NullCoalescingRewritten;
        _statistics.NullCoalescingAssignRewritten += source.NullCoalescingAssignRewritten;
        _statistics.TempVariablesGenerated += source.TempVariablesGenerated;
        _statistics.NestedRewrites += source.NestedRewrites;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
        _statistics.ExpressionBodiesExpanded += source.ExpressionBodiesExpanded;
        _statistics.ResidualCount += source.ResidualCount;
        _statistics.DeferredDownstreamSites += source.DeferredDownstreamSites;
    }
}
