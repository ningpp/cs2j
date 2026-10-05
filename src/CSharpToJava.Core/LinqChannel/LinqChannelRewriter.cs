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
/// attaches the switchable route gate and runs the stock
/// <see cref="LinqRewriter"/>.
/// <para>
/// M2 gate semantics (design §4.2/§4.3): with
/// <see cref="LinqChannelOptions.EnableQueryableGate"/> = true (default) the
/// gate is INTERCEPTING — Queryable/ExpressionArgument/Mixed/Ambiguous chains
/// are preserved wholesale while Enumerable/NonLinq chains expand exactly as
/// E16; with the option false the gate is OBSERVE-ONLY (counts, never changes
/// output, M1 behavior). A null gate remains the embedded E16 construction and
/// is byte-identical to the pre-C26 rewriter.
/// </para>
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

        // The gate is always attached inside the channel: option off = M1
        // observe-only (counts, output identical to E16); option on = M2
        // intercept (preserved chains freeze before the support gate).
        ILinqRewriteGate gate = new RoutingGate(
            semanticModel, statistics, plan, _options.EnableQueryableGate);

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
    /// Switchable route gate at the rewriter's two entry points.
    /// <para>
    /// Observation (third-route expression sites + provider-custom counters)
    /// happens in both modes; each invocation is tallied at most once even
    /// when both the foreach hook and the generic invocation visitor see it on
    /// the foreach-rewrap fallback path.
    /// </para>
    /// <para>
    /// Intercept mode answers preserve from the per-file plan: every node of a
    /// non-Expandable chain (Queryable/Mixed/Ambiguous) freezes the whole
    /// invocation subtree when visited, so the rewriter never reaches the
    /// support gate or the syntax-name fallback for those nodes (design
    /// §4.2/§4.3; the LC0011 hard invariant is an M3 addition).
    /// </para>
    /// </summary>
    private sealed class RoutingGate : ILinqRewriteGate
    {
        private readonly LinqChannelStatistics _statistics;
        private readonly LinqRouteClassifier _classifier;
        private readonly bool _intercept;
        private readonly HashSet<InvocationExpressionSyntax> _observed = new();
        private readonly HashSet<InvocationExpressionSyntax> _preservedNodes;

        public RoutingGate(
            SemanticModel semanticModel,
            LinqChannelStatistics statistics,
            LinqChannelPlan plan,
            bool intercept)
        {
            _statistics = statistics;
            _classifier = new LinqRouteClassifier(semanticModel);
            _intercept = intercept;
            _preservedNodes = plan.Chains
                .Where(chain => chain.Decision.Route != LinqChainRoute.Expandable)
                .SelectMany(chain => chain.Nodes)
                .ToHashSet();

            // In intercept mode a preserved outer node freezes its whole
            // subtree, so the observe hooks never fire for its nodes (nor for
            // chains nested inside quoted lambdas). Observation counters must
            // stay mode-independent, so every plan node is tallied up front;
            // visit-time Tally calls dedupe against this set.
            foreach (var node in plan.Chains.SelectMany(chain => chain.Nodes))
            {
                Tally(node);
            }
        }

        public void ObserveInvocation(InvocationExpressionSyntax invocation) => Tally(invocation);

        public void ObserveForeachCollection(InvocationExpressionSyntax collection) => Tally(collection);

        public bool ShouldPreserveInvocation(InvocationExpressionSyntax invocation)
        {
            if (!_intercept)
            {
                // Observe-only (M1 shape): the support gate and the name-guess
                // fallback stay exactly where they were.
                return false;
            }

            Tally(invocation);
            return _preservedNodes.Contains(invocation);
        }

        public bool ShouldPreserveForeachCollection(InvocationExpressionSyntax collection)
            => ShouldPreserveInvocation(collection);

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
