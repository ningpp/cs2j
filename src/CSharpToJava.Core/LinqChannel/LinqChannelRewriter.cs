using CSharpToJava.Core.Analysis;
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
/// M3 gate semantics (design §4.2/§4.3/§4.6): the gate is PERMANENTLY
/// intercepting — Queryable/ExpressionArgument/Mixed/Ambiguous chains, every
/// node lexically inside an <c>Expression&lt;&gt;</c>-quoted lambda and
/// unprovable quoted sites are frozen node for node; only Enumerable/NonLinq
/// chains outside quoted regions expand, exactly as E16. The M1/M2
/// observe-only off-switch (<c>EnableQueryableGate</c>) was removed. A null
/// gate remains the embedded E16 construction and is byte-identical to the
/// pre-C26 rewriter; the LC0011 tripwires are no-ops on that path.
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

        // M3: the gate is permanently intercepting inside the channel. It
        // precomputes the plan-preserve set and the quoted-expression region
        // set (including LC0012 unprovable sites) and carries the LC0011
        // tripwires. The embedded E16 path constructs LinqRewriter with no
        // gate and is untouched.
        ILinqRewriteGate gate = new RoutingGate(
            semanticModel, statistics, plan, diagnostics, _options.Strict, desugaredRoot);

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
            // M3 (§4.4): an otherwise-expandable chain inside a quoted
            // Expression<> lambda is frozen and counted separately — no node
            // inside a quote may be proceduralized (M2 leftover fix).
            if (chain.Decision.Route == LinqChainRoute.Expandable && chain.InsideQuotedLambda)
            {
                statistics.ExpressionQuotedChainsPreserved++;
                AddPreservedChain(statistics, plan.FilePath, chain, "PreserveExpressionQuoted");
                continue;
            }

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
        LinqChannelPlan.ChainEntry chain,
        string? routeOverride = null)
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
            routeOverride ?? chain.Decision.Route.ToString(),
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
    /// M3 permanently-intercepting route gate at the rewriter's two entry
    /// points (design §4.2/§4.3/§4.4/§4.6).
    /// <para>
    /// Observation (third-route expression sites + provider-custom counters)
    /// covers every plan node and every quoted-region invocation; each
    /// invocation is tallied at most once even when both the foreach hook and
    /// the generic invocation visitor see it on the foreach-rewrap path.
    /// </para>
    /// <para>
    /// The freeze set is the union of (a) every node of a chain the plan says
    /// must be preserved (Queryable/Mixed/Ambiguous, plus any chain inside a
    /// quoted lambda — <see cref="LinqChannelPlan.ChainEntry.MustPreserve"/>)
    /// and (b) EVERY invocation lexically inside an Expression&lt;&gt;-quoted
    /// lambda, whether or not it belongs to a discovered LINQ chain (the M2
    /// leftover: a quoted inner Enumerable chain or a user/Expression-API call
    /// inside the tree must not be proceduralized). Freezing returns the
    /// original node, so neither the support gate nor the syntax-name fallback
    /// is ever reached for those nodes.
    /// </para>
    /// <para>
    /// The two Guard* methods are the LC0011 anti-regression tripwires: they
    /// throw <see cref="LinqRouteInvariantException"/> if a freeze-set node
    /// reaches the expansion branch or the <c>n == null</c> fallback fabricates
    /// an Enumerable identity for a LINQ operator while the gate is attached.
    /// Correct routing makes both unreachable.
    /// </para>
    /// </summary>
    private sealed class RoutingGate : ILinqRewriteGate
    {
        private readonly LinqChannelStatistics _statistics;
        private readonly LinqRouteClassifier _classifier;
        private readonly List<LinqChannelDiagnostic> _diagnostics;
        private readonly bool _strict;
        private readonly string? _filePath;
        private readonly HashSet<InvocationExpressionSyntax> _observed = new();
        private readonly HashSet<InvocationExpressionSyntax> _freezeSet;
        private readonly HashSet<InvocationExpressionSyntax> _planChainNodes;
        private readonly HashSet<InvocationExpressionSyntax> _lc0010Nodes;

        public RoutingGate(
            SemanticModel semanticModel,
            LinqChannelStatistics statistics,
            LinqChannelPlan plan,
            List<LinqChannelDiagnostic> diagnostics,
            bool strict,
            SyntaxNode root)
        {
            _statistics = statistics;
            _classifier = new LinqRouteClassifier(semanticModel);
            _diagnostics = diagnostics;
            _strict = strict;
            _filePath = plan.FilePath;

            _planChainNodes = plan.Chains
                .SelectMany(chain => chain.Nodes)
                .ToHashSet();
            _lc0010Nodes = plan.Chains
                .Where(chain => chain.Decision.Route == LinqChainRoute.PreserveAmbiguous)
                .SelectMany(chain => chain.Nodes
                    .Zip(chain.Decision.Nodes)
                    .Where(pair => pair.Second.Kind == LinqRouteKind.Ambiguous)
                    .Select(pair => pair.First))
                .ToHashSet();

            // (a) plan-preserved chain nodes.
            var freezeSet = plan.Chains
                .Where(chain => chain.MustPreserve)
                .SelectMany(chain => chain.Nodes)
                .ToHashSet();

            // (b) every invocation inside an Expression<>-quoted lambda,
            // whether or not it is part of a discovered LINQ chain. C# has no
            // unquote/splice syntax in plain source, so lexical containment is
            // sufficient.
            foreach (var invocation in root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>()
                         .Where(i => !LinqRouteClassifier.IsNameofPseudoCall(i))
                         .Where(i => ExpressionTreeFacts.IsInsideQuotedExpressionLambda(semanticModel, i)))
            {
                freezeSet.Add(invocation);
                ReportIfUnprovable(invocation);
            }

            _freezeSet = freezeSet;

            // A frozen outer node freezes its whole subtree, so the observe
            // hooks never fire for nested nodes during the visit. Observation
            // counters must be visit-order independent; tally every plan node
            // up front (visit-time Tally calls dedupe against _observed).
            foreach (var node in _planChainNodes)
            {
                Tally(node);
            }

            foreach (var node in freezeSet)
            {
                Tally(node);
            }
        }

        public void ObserveInvocation(InvocationExpressionSyntax invocation) => Tally(invocation);

        public void ObserveForeachCollection(InvocationExpressionSyntax collection) => Tally(collection);

        public bool ShouldPreserveInvocation(InvocationExpressionSyntax invocation)
        {
            Tally(invocation);
            return _freezeSet.Contains(invocation);
        }

        public bool ShouldPreserveForeachCollection(InvocationExpressionSyntax collection)
            => ShouldPreserveInvocation(collection);

        public void GuardEnterExpansion(InvocationExpressionSyntax invocation)
        {
            if (_freezeSet.Contains(invocation))
            {
                throw Invariant(
                    invocation,
                    "a node routed Queryable/Expression/Mixed/Ambiguous (or located inside an "
                    + "Expression<>-quoted lambda) reached the stock rewriter's expansion branch; "
                    + "the three-way gate lost a node (LC0011, design §4.3/§6)");
            }
        }

        public void GuardSyntaxNameFallback(InvocationExpressionSyntax invocation, string syntaxName)
        {
            // The caller only reaches this for member-access invocations whose
            // member token is a known LINQ operator name while the bound symbol
            // is null: the fabricated IEnumerable<T> identity hole (R2). With
            // the channel gate attached this may never succeed.
            throw new LinqRouteInvariantException(
                $"LINQ operator '{syntaxName}' did not bind a symbol and the GetMethodFullName "
                + "syntax-name fallback was about to fabricate a System.Linq.Enumerable identity in "
                + "LINQ chain context while the channel gate was active (LC0011, design §4.3/R2); "
                + "the file is preserved wholesale",
                _filePath,
                LineOf(invocation));
        }

        /// <summary>
        /// M3 LC0012 (CS2J1303): a quoted-tree site the channel cannot prove
        /// identity for. BCL surface (<c>System.*</c> — Enumerable/Queryable
        /// operators, String/DateTime/… instance methods, the
        /// System.Linq.Expressions.Expression builder API) keeps its
        /// MethodInfo after rebind and is silent; calls bound outside the BCL
        /// surface (user/3rd-party methods, provider extensions such as EF
        /// <c>Include</c>) and unbound calls are preserved and reported.
        /// Ambiguous nodes already carrying LC0010 are not double-reported.
        /// </summary>
        private void ReportIfUnprovable(InvocationExpressionSyntax invocation)
        {
            var decision = _classifier.ClassifyInvocation(invocation);
            if (decision.Kind == LinqRouteKind.Ambiguous)
            {
                if (_lc0010Nodes.Contains(invocation))
                {
                    return;
                }
            }
            else if (decision.Kind == LinqRouteKind.NonLinq
                && IsProvableBclHost(decision.BoundMethod?.ContainingType))
            {
                return;
            }
            else if (decision.Kind is LinqRouteKind.Enumerable
                or LinqRouteKind.Queryable)
            {
                return;
            }

            var severity = _strict
                ? LinqChannelDiagnosticSeverity.Error
                : LinqChannelDiagnosticSeverity.Warning;
            var syntaxName = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                ?? invocation.Expression.ToString();
            var host = decision.BoundMethod?.ContainingType.ToDisplayString() ?? decision.HostDisplay ?? "<unbound>";
            _diagnostics.Add(new LinqChannelDiagnostic(
                severity,
                LinqChannelDiagnosticCodes.ExpressionTreeUnprovable,
                $"Call '{syntaxName}' inside a quoted Expression<> tree binds to '{host}', whose "
                + "MethodInfo identity the channel cannot prove after rebind (LC0012); the node is preserved",
                _filePath,
                LineOf(invocation)));
            _statistics.ExpressionTreeUnprovableSites++;
        }

        private static bool IsProvableBclHost(ISymbol? containingType)
        {
            if (containingType is null)
            {
                return false;
            }

            var display = containingType.ToDisplayString();
            return display.StartsWith("System.", StringComparison.Ordinal);
        }

        private LinqRouteInvariantException Invariant(
            InvocationExpressionSyntax invocation, string message)
            => new(message, _filePath, LineOf(invocation));

        private static int? LineOf(SyntaxNode node)
            => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

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
