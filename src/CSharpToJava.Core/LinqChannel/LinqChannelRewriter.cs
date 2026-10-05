using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Outcome of the channel pipeline for one file (design §4.5).
/// </summary>
public sealed record LinqChannelRewriteResult
{
    public required SyntaxNode DesugaredRoot { get; init; }

    public bool DesugaredChanged { get; init; }

    /// <summary>Stage-2 output (post-desugar root run through the gated LinqRewriter).</summary>
    public required SyntaxNode RewrittenRoot { get; init; }

    public bool RewriteChanged { get; init; }

    public required LinqChannelStatistics Statistics { get; init; }

    public required List<LinqChannelDiagnostic> Diagnostics { get; init; }

    /// <summary>Query expressions a second desugar pass over the output would still lower.</summary>
    public int SecondPassDesugaredCount { get; init; }

    /// <summary>True when a second desugar pass over the output makes no change (M1 syntactic idempotency).</summary>
    public bool SyntacticallyIdempotent => SecondPassDesugaredCount == 0;
}

/// <summary>
/// Channel-side orchestration entry (NOT a new rewriter): stage 1 reuses
/// <see cref="LinqQueryDesugarer"/> verbatim; stage 2 builds the routing plan,
/// attaches the M1 observe-only gate and runs the stock
/// <see cref="LinqRewriter"/>. Because the gate never alters rewrite decisions,
/// stage-2 output is identical to the embedded E16 run over the same tree
/// (M1 exit criterion; M2 adds byte-level equivalence tests).
/// </summary>
public sealed class LinqChannelRewriter
{
    private readonly LinqChannelOptions _options;

    public LinqChannelRewriter(LinqChannelOptions? options = null) =>
        _options = options ?? new LinqChannelOptions();

    /// <summary>
    /// Stage 1: pure-syntactic query-expression desugaring. No semantic model;
    /// safe to run over the whole tree in parallel (design §4.5 step 2).
    /// </summary>
    public static (SyntaxNode Root, int DesugaredCount) DesugarQueries(SyntaxNode root)
    {
        var desugarer = new LinqQueryDesugarer();
        var visited = desugarer.Visit(root) ?? root;
        return (visited, desugarer.DesugaredCount);
    }

    /// <summary>
    /// Stage 2: route + rewrite one file. <paramref name="semanticModel"/> MUST
    /// be built over <paramref name="desugaredRoot"/> (the adapter rebuilds the
    /// whole-tree compilation from the stage-1 intermediate tree on disk).
    /// </summary>
    public LinqChannelRewriteResult ProcessFile(
        SemanticModel semanticModel,
        SyntaxNode desugaredRoot,
        string? filePath,
        ConversionOptions? conversionOptions = null)
    {
        var statistics = new LinqChannelStatistics();
        var diagnostics = new List<LinqChannelDiagnostic>();

        // Route plan drives chain-level counters, the preserved-chain inventory
        // and LC0010 diagnostics deterministically (independent of which
        // rewriter entry visits a node).
        var plan = LinqChannelPlan.Build(semanticModel, desugaredRoot, filePath);
        TallyPlan(plan, statistics, diagnostics);

        ILinqRewriteGate? gate = _options.EnableQueryableGate
            ? new ObserveOnlyGate(semanticModel, statistics)
            : null;

        var rewriter = new LinqRewriter(semanticModel, conversionOptions, gate);
        var rewrittenRoot = rewriter.Visit(desugaredRoot) ?? desugaredRoot;
        statistics.Rewrite.MergeFrom(rewriter.Statistics);

        // M1 idempotency self-check (design §4.1③): the output must contain no
        // query expression the desugarer would still lower. Full cross-stage
        // idempotency (rebuild + second rewrite yields no diff) is an adapter
        // level check (M2/M3 residual gate).
        var (_, secondPassDesugaredCount) = DesugarQueries(rewrittenRoot);

        return new LinqChannelRewriteResult
        {
            DesugaredRoot = desugaredRoot,
            DesugaredChanged = false,
            RewrittenRoot = rewrittenRoot,
            RewriteChanged = !rewrittenRoot.IsEquivalentTo(desugaredRoot),
            Statistics = statistics,
            Diagnostics = diagnostics,
            SecondPassDesugaredCount = secondPassDesugaredCount,
        };
    }

    /// <summary>Convenience for tests: stage 1 then stage 2 against a rebuilt model.</summary>
    public (LinqChannelRewriteResult Result, int DesugaredCount) RunFromOriginal(
        SemanticModel desugaredModel,
        SyntaxNode originalRoot,
        string? filePath = null,
        ConversionOptions? conversionOptions = null)
    {
        var (desugaredRoot, desugaredCount) = DesugarQueries(originalRoot);
        var result = ProcessFile(desugaredModel, desugaredRoot, filePath, conversionOptions)
            with { DesugaredChanged = !desugaredRoot.IsEquivalentTo(originalRoot) };
        return (result, desugaredCount);
    }

    private void TallyPlan(
        LinqChannelPlan plan,
        LinqChannelStatistics statistics,
        List<LinqChannelDiagnostic> diagnostics)
    {
        foreach (var chain in plan.Chains)
        {
            switch (chain.Decision.Route)
            {
                case LinqChainRoute.Expandable:
                    statistics.EnumerableChainsExpanded++;
                    break;
                case LinqChainRoute.PreserveQueryable:
                    statistics.QueryableChainsPreserved++;
                    AddPreservedChain(statistics, plan.FilePath, chain);
                    break;
                case LinqChainRoute.PreserveMixed:
                    statistics.MixedChainsPreserved++;
                    AddPreservedChain(statistics, plan.FilePath, chain);
                    diagnostics.Add(MixedInfo(plan.FilePath, chain));
                    break;
                case LinqChainRoute.PreserveAmbiguous:
                    statistics.AmbiguousChains++;
                    AddPreservedChain(statistics, plan.FilePath, chain);
                    AddAmbiguousDiagnostics(diagnostics, plan.FilePath, chain);
                    break;
            }
        }
    }

    private static void AddPreservedChain(
        LinqChannelStatistics statistics,
        string? filePath,
        LinqChannelPlan.ChainEntry chain)
    {
        var outer = chain.Nodes[0];
        var line = outer.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var outerDecision = chain.Decision.Nodes[0];
        var operators = chain.Nodes
            .Zip(chain.Decision.Nodes)
            .Where(pair => pair.Second.Kind is LinqRouteKind.Enumerable
                or LinqRouteKind.Queryable
                or LinqRouteKind.Ambiguous)
            .Select(pair => (pair.First.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                ?? pair.First.Expression.ToString())
            .ToList();

        statistics.PreservedChains.Add(new LinqPreservedChain(
            filePath ?? string.Empty,
            line,
            chain.Decision.Route.ToString(),
            outerDecision.HostDisplay,
            operators));
    }

    private static LinqChannelDiagnostic MixedInfo(string? filePath, LinqChannelPlan.ChainEntry chain)
    {
        var outer = chain.Nodes[0];
        var line = outer.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var names = string.Join(".", chain.Nodes
            .Select(n => (n.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text ?? n.Expression.ToString()));
        return new LinqChannelDiagnostic(
            LinqChannelDiagnosticSeverity.Info,
            LinqChannelDiagnosticCodes.MixedChainPreserved,
            $"Mixed-host LINQ chain preserved wholesale (D6): {names}",
            filePath,
            line);
    }

    private void AddAmbiguousDiagnostics(
        List<LinqChannelDiagnostic> diagnostics,
        string? filePath,
        LinqChannelPlan.ChainEntry chain)
    {
        for (var i = 0; i < chain.Nodes.Count; i++)
        {
            var decision = chain.Decision.Nodes[i];
            if (decision.Kind != LinqRouteKind.Ambiguous)
            {
                continue;
            }

            var node = chain.Nodes[i];
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var syntaxName = (node.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                ?? node.Expression.ToString();
            var severity = _options.Strict
                ? LinqChannelDiagnosticSeverity.Error
                : LinqChannelDiagnosticSeverity.Warning;
            var message = decision.Ambiguity == LinqRouteAmbiguity.UnsupportedExpressionNode
                ? $"LINQ operator '{syntaxName}' has an Expression<> argument on an Enumerable host; node preserved (LC0010)."
                : $"LINQ-style call '{syntaxName}' did not bind a unique symbol ({decision.Ambiguity}, {decision.CandidateReason}); node preserved without guessing Enumerable (LC0010).";

            diagnostics.Add(new LinqChannelDiagnostic(
                severity,
                LinqChannelDiagnosticCodes.AmbiguousPreserved,
                message,
                filePath,
                line));
        }
    }

    /// <summary>
    /// M1 observe-only gate: records the third-route and provider-custom
    /// counters at the rewriter's two entry points. Each invocation is tallied
    /// at most once even when both the foreach hook and the generic invocation
    /// visitor see it on the foreach-rewrap fallback path.
    /// </summary>
    private sealed class ObserveOnlyGate : ILinqRewriteGate
    {
        private readonly SemanticModel _semanticModel;
        private readonly LinqChannelStatistics _statistics;
        private readonly LinqRouteClassifier _classifier;
        private readonly HashSet<InvocationExpressionSyntax> _observed = new();

        public ObserveOnlyGate(SemanticModel semanticModel, LinqChannelStatistics statistics)
        {
            _semanticModel = semanticModel;
            _statistics = statistics;
            _classifier = new LinqRouteClassifier(semanticModel);
        }

        public void ObserveInvocation(InvocationExpressionSyntax invocation) => Tally(invocation);

        public void ObserveForeachCollection(InvocationExpressionSyntax collection) => Tally(collection);

        private void Tally(InvocationExpressionSyntax invocation)
        {
            if (!_observed.Add(invocation) || LinqRouteClassifier.IsNameofPseudoCall(invocation))
            {
                return;
            }

            var decision = _classifier.ClassifyInvocation(invocation);
            if (decision.ExpressionArgumentCount > 0)
            {
                _statistics.ExpressionArgumentSites += decision.ExpressionArgumentCount;
            }

            // D3②: LINQ-lookalike operator names bound to a non-BCL host
            // (custom query-provider extensions, user Where/Select). Reported
            // and never touched; Include/ThenInclude-style provider APIs are
            // not System.Linq operator names and surface in the M3 matrix.
            if (decision.Kind == LinqRouteKind.NonLinq
                && LinqRouteClassifier.HasLinqOperatorName(invocation))
            {
                _statistics.ProviderCustomCalls++;
            }
        }
    }
}
