using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// C35 null-conditional read-side rule (fixed family order: third, after
/// Initializers(C24) and before CollectionExpr(C36)).
///
/// <para>
/// Rollout gate (same pattern as C24): the rule is inert unless the host opts
/// in by supplying the <see cref="ExpressionDesugarOptions.NullConditional"/>
/// subsection. Trigger syntax still reports dirty so the rule participates
/// uniformly, but without the subsection its plan rewrites nothing.
/// </para>
///
/// Analyze builds one <see cref="ChainPlan"/> per chain root via the
/// SemanticModel-driven <see cref="ChainSegmentAnalyzer"/>; Rewrite runs one
/// bottom-up <see cref="NullConditionalRewriter"/>. Assignment terminals
/// (NC-7) are preserved for the later C51 rule in this same directory.
/// </summary>
internal sealed class NullConditionalRule : IExpressionDesugarRule
{
    public string Id => "NullConditional";

    public bool IsPotentiallyDirty(
        ExpressionDesugarSyntaxProfile profile,
        ExpressionDesugarOptions options)
        // The rollout gate lives in Analyze (Options.NullConditional subsection).
        => profile.HasConditionalAccess;

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        if (context.Options.NullConditional is null)
        {
            return new NullConditionalRulePlan(
                context,
                new NullConditionalAnalysis(Array.Empty<ChainPlan>()));
        }

        return new NullConditionalRulePlan(
            context,
            ChainSegmentAnalyzer.Analyze(context));
    }

    /// <summary>Analyze product: owns plans and drives the bottom-up rewrite.</summary>
    private sealed class NullConditionalRulePlan : ExpressionDesugarRulePlan
    {
        private const string MarkerKind = "ed-nullcond-site";

        private readonly RuleAnalysisContext _context;
        private readonly NullConditionalAnalysis _analysis;
        private readonly RuleSiteMarkers<ChainPlan> _markers = new(MarkerKind);

        /// <summary>Original anchor -> family-lift annotation (pre-stamped).</summary>
        private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations = new();

        /// <summary>
        /// Plans that splice their anchor statement -> per-plan replacement
        /// marker (pre-stamped on the original anchor).
        /// </summary>
        private readonly Dictionary<ChainPlan, SyntaxAnnotation> _anchorReplacementsByPlan = new();
        private NullConditionalRewriter? _rewriter;
        private NullConditionalRewriter? _lateRewriter;
        private IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> _lateAnchorAnnotations
            = new Dictionary<SyntaxNode, SyntaxAnnotation>();

        public NullConditionalRulePlan(
            RuleAnalysisContext context,
            NullConditionalAnalysis analysis)
        {
            _context = context;
            _analysis = analysis;
            // C35 runs third: earlier rules (C23/C24) refresh node instances,
            // so chain roots and anchors resolve through stable markers.
            foreach (var plan in analysis.Plans)
            {
                _markers.Add(plan.RootOriginal, plan);

                if (plan.Anchor is not { } anchor)
                    continue;

                // Every anchored plan may register TempLifter pending temps.
                if (!_anchorAnnotations.ContainsKey(anchor))
                {
                    _anchorAnnotations[anchor] = new SyntaxAnnotation(
                        ExpressionDesugarTempLifter.AnchorAnnotationKind);
                }

                if (plan.Shape is NullConditionalLoweringShape.StatementIf
                    or NullConditionalLoweringShape.GuardedBlock)
                {
                    _anchorReplacementsByPlan[plan] =
                        new SyntaxAnnotation("ed-nc-anchor-replacement");
                }
            }
        }

        public override void ContributeStamps(
            Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
        {
            _markers.ContributeTo(stamps);
            foreach (var pair in _anchorAnnotations)
                AddStamp(stamps, pair.Key, pair.Value);
            foreach (var pair in _anchorReplacementsByPlan)
                AddStamp(stamps, pair.Key.Anchor!, pair.Value);
        }

        private static void AddStamp(
            Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps,
            SyntaxNode node,
            SyntaxAnnotation annotation)
        {
            if (!stamps.TryGetValue(node, out var list))
            {
                list = new List<SyntaxAnnotation>();
                stamps[node] = list;
            }

            list.Add(annotation);
        }

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            _rewriter = new NullConditionalRewriter(
                _analysis, _context.SemanticModel!, _markers,
                _anchorAnnotations, _anchorReplacementsByPlan);
            var visited = (CompilationUnitSyntax)_rewriter.Visit(root)!;

            // Replace whole anchor statements for void/guarded-block shapes
            // before the family TempLifter runs (design §4.4).
            return PendingAnchorReplacementApplicator.Apply(
                visited, _rewriter.AnchorReplacements);
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)_rewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => (IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation>?)_rewriter?.AnchorAnnotations
               ?? _anchorAnnotations;

        public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

        public override bool HasDeferredSites
            => _analysis.Plans.Any(static plan => plan.AnchorDeferred);

        /// <summary>
        /// Post-lift late rewrite (design hard interaction with C24/C36):
        /// chains carried INSIDE another rule's pending statement sequences
        /// (e.g. a null-conditional in an object initializer member value)
        /// are invisible to the primary rewrite because those sequences are
        /// spliced into the tree only by the family TempLifter. Once they are
        /// in the tree their site markers resolve again; anchor their pending
        /// declarations against the CURRENT hosting statements and let one
        /// additional family lift insert them.
        /// </summary>
        public override CompilationUnitSyntax? LateRewriteAfterLift(
            CompilationUnitSyntax root)
        {
            if (_context.Options.NullConditional is null)
                return null;

            var deferredPlans = _analysis.Plans
                .Where(static plan => plan.AnchorDeferred)
                .ToList();

            // Pre-scan: every planned chain root still present in the
            // post-lift tree was unreachable during the primary rewrite
            // (lowered roots disappeared with their expressions; preserved
            // roots were explained in place).
            var anchorByPlan = new Dictionary<ChainPlan, StatementSyntax>();
            var anchors = new List<StatementSyntax>();
            var anchorSet = new HashSet<StatementSyntax>();
            var replacementPlans = new List<ChainPlan>();

            foreach (var ca in root.DescendantNodes()
                         .OfType<ConditionalAccessExpressionSyntax>())
            {
                var plan = _markers.TryResolve(ca);
                if (plan == null)
                    continue;

                // Already paired by the primary rewrite (preserved sites keep
                // the node plus the explained annotation).
                if (plan.Outcome == ChainOutcome.Preserved
                    && NullConditionalSyntaxFacts.IsExplained(ca))
                {
                    continue;
                }

                if (ExpressionDesugarTempLifter.FindAnchor(ca)
                    is not StatementSyntax anchor)
                {
                    continue;
                }

                anchorByPlan[plan] = anchor;
                if (anchorSet.Add(anchor))
                    anchors.Add(anchor);
                if (plan.Shape is NullConditionalLoweringShape.StatementIf
                    or NullConditionalLoweringShape.GuardedBlock
                    && !replacementPlans.Contains(plan))
                {
                    replacementPlans.Add(plan);
                }
            }

            if (anchorByPlan.Count == 0 && deferredPlans.Count == 0)
                return null;

            CompilationUnitSyntax result = root;

            if (anchorByPlan.Count > 0)
            {
                // Stamp FRESH lift/replacement annotations on the current
                // hosting statements (the plans' analyze-time anchors precede
                // those statements or were replaced by the primary lift).
                var stamps = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
                var anchorAnnotations =
                    new Dictionary<SyntaxNode, SyntaxAnnotation>();
                var anchorAnnotationsByStatement =
                    new Dictionary<StatementSyntax, SyntaxAnnotation>();
                foreach (var anchor in anchors)
                {
                    var annotation = new SyntaxAnnotation(
                        ExpressionDesugarTempLifter.AnchorAnnotationKind);
                    anchorAnnotations[anchor] = annotation;
                    anchorAnnotationsByStatement[anchor] = annotation;
                    AddStamp(stamps, anchor, annotation);
                }

                var replacementMarkers = new Dictionary<ChainPlan, SyntaxAnnotation>();
                foreach (var plan in replacementPlans)
                {
                    var marker = new SyntaxAnnotation("ed-nc-anchor-replacement");
                    replacementMarkers[plan] = marker;
                    AddStamp(stamps, anchorByPlan[plan], marker);
                }

                var stamped = StableMarkerStamper.Apply(root, stamps);

                var lateContext = new NullConditionalLateContext
                {
                    AnchorByPlan = anchorByPlan,
                    AnchorAnnotations = anchorAnnotationsByStatement,
                    ReplacementMarkers = replacementMarkers,
                };

                _lateRewriter = new NullConditionalRewriter(
                    _analysis, _context.SemanticModel!, _markers,
                    _anchorAnnotations, _anchorReplacementsByPlan, lateContext);
                var visited = (CompilationUnitSyntax)_lateRewriter.Visit(stamped)!;
                result = PendingAnchorReplacementApplicator.Apply(
                    visited, _lateRewriter.AnchorReplacements);

                _lateAnchorAnnotations = anchorAnnotations;
            }

            return ResolveDeferredPlans(result, deferredPlans);
        }

        /// <summary>
        /// Settles anchor-deferred chains after the late rewrite: chains that
        /// left the tree acquired a hosting statement during the family lift
        /// and are now lowered (emit success Info/stats); chains still present
        /// are genuinely anchor-free and receive CS2J0557 (strict Error /
        /// non-strict Warning) plus the explained annotation pairing the gate.
        /// </summary>
        private CompilationUnitSyntax ResolveDeferredPlans(
            CompilationUnitSyntax result, IReadOnlyList<ChainPlan> deferredPlans)
        {
            if (deferredPlans.Count == 0)
                return result;

            var explainStamps =
                new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
            var strict = _context.Options.NullConditional!.Strict;

            foreach (var plan in deferredPlans)
            {
                var remaining = result.DescendantNodes()
                    .OfType<ConditionalAccessExpressionSyntax>()
                    .FirstOrDefault(c => _markers.TryResolve(c) == plan);

                if (remaining == null)
                {
                    ChainSegmentAnalyzer.AccumulateLowered(plan, _context);
                    continue;
                }

                _context.Report(
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    NullConditionalDiagnosticCodes.CS2J0557,
                    string.Format(
                        NullConditionalDiagnostics.UnliftableMessage,
                        plan.RootOriginal.ToString()),
                    remaining);
                _context.Statistics.NullConditional.AddSkip(
                    NullConditionalDiagnosticCodes.CS2J0557);
                AddStamp(
                    explainStamps, remaining,
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
            }

            return explainStamps.Count > 0
                ? StableMarkerStamper.Apply(result, explainStamps)
                : result;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>
            LatePendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)
                   _lateRewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation>
            LateAnchorAnnotations
            => _lateAnchorAnnotations;

        public override int CheckResiduals(
            RuleAnalysisContext context, CompilationUnitSyntax root)
            => NullConditionalResidualGate.Check(context, root);
    }
}

/// <summary>
/// Post-lift residual gate for the C35/C51 directory (design §6, NA-8):
/// every trigger node left in the output must be paired with an explained
/// preservation. Extracted as an internal static gate so it stays directly
/// testable without constructing a private rule plan.
/// </summary>
internal static class NullConditionalResidualGate
{
    public static int Check(
        RuleAnalysisContext context, CompilationUnitSyntax root)
    {
        // Inert when the subsection is absent (rollout gate).
        if (context.Options.NullConditional is null)
            return 0;

        var unpaired = 0;
        SyntaxNode? firstResidual = null;
        var stats = context.Statistics.NullConditional;

        foreach (var node in root.DescendantNodes())
        {
            // NA-8 explicit gate: a residual CA whose WhenNotNull is an
            // assignment expression (every assignment terminal must be
            // lowered or carry an explained preservation).
            var assignmentResidual = node is ConditionalAccessExpressionSyntax residualCa
                && StripTerminal(residualCa) is AssignmentExpressionSyntax;

            if (!NullConditionalSyntaxFacts.IsResidualNode(node)
                && !assignmentResidual)
                continue;
            if (NullConditionalSyntaxFacts.IsExplained(node))
                continue;

            unpaired++;
            firstResidual ??= node;

            if (assignmentResidual)
                stats.AssignmentResidualNodeCount++;
        }

        stats.ResidualNodeCount = unpaired;
        if (unpaired > 0)
        {
            var strict = context.Options.NullConditional.Strict;
            context.Report(
                NullConditionalDiagnostics.ResolveStrictError(strict),
                NullConditionalDiagnosticCodes.CS2J0566,
                string.Format(
                    NullConditionalDiagnostics.ResidualMessage, unpaired),
                firstResidual);
        }

        return unpaired;
    }

    /// <summary>
    /// A CA's terminal with surrounding parentheses stripped (assignment
    /// terminals are never wrapped, but parens survive on illegal inputs).
    /// </summary>
    private static ExpressionSyntax StripTerminal(ConditionalAccessExpressionSyntax ca)
    {
        var terminal = ca.WhenNotNull;
        while (terminal.IsKind(SyntaxKind.ParenthesizedExpression))
            terminal = ((ParenthesizedExpressionSyntax)terminal).Expression;
        return terminal;
    }
}
