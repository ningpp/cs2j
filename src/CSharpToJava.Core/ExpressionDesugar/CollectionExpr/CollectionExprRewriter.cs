using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Bottom-up rewriter for C36 (design §4.1 step 2). Inner collection
/// expressions expand first; zero-temp forms replace the node directly while
/// spread-bearing forms register <see cref="ExpressionDesugarTempLifter.PendingTemp"/>
/// statement sequences at the anchor found by the shared lifter and replace the
/// site with <c>__ed_n</c> / <c>__ed_n.ToArray()</c>.
///
/// All semantic decisions come from Analyze-built plans keyed by ORIGINAL
/// nodes; rendering uses the already-visited children (C47 discipline).
/// Skipped plans keep the node and annotate it for the residual gate.
/// </summary>
internal sealed class CollectionExprRewriter : CSharpSyntaxRewriter
{
    private readonly RuleAnalysisContext _context;
    private readonly IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> _plans;
    private readonly RuleSiteMarkers<CollectionExprPlan> _markers;
    private readonly IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations;

    public CollectionExprRewriter(
        RuleAnalysisContext context,
        IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> plans,
        RuleSiteMarkers<CollectionExprPlan> markers,
        IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> anchorAnnotations)
    {
        _context = context;
        _plans = plans;
        _markers = markers;
        _anchorAnnotations = anchorAnnotations;
    }

    /// <summary>Statement sequences collected for the family TempLifter.</summary>
    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Anchor node -> annotation map consumed by TempLifter.Apply.</summary>
    public IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
        => _anchorAnnotations;

    /// <summary>Number of sites actually rewritten (drives the family dirty gate).</summary>
    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitCollectionExpression(CollectionExpressionSyntax node)
    {
        var visited = (CollectionExpressionSyntax)base.VisitCollectionExpression(node)!;

        // Stable marker first (earlier rules refresh node instances); identity
        // lookup stays as a defensive fallback.
        var plan = _markers.TryResolve(visited)
            ?? (_plans.TryGetValue(node, out var originalPlan) ? originalPlan : null);
        if (plan == null)
            return visited;

        var stats = _context.Statistics.CollectionExpr;

        if (plan.Outcome == CollectionExprOutcome.SkippedDiagnostic)
        {
            // Preserved site: paired with its skip diagnostic for CE-12.
            return visited.WithAdditionalAnnotations(
                ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }

        switch (plan.Outcome)
        {
            case CollectionExprOutcome.ZeroTempArray:
            {
                RewriteCount++;
                var replacement = CollectionExprEmitter.BuildZeroTempArray(
                    plan.TargetElementType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));
                CountEmittedInitializerNodes(replacement, visited);
                return replacement;
            }

            case CollectionExprOutcome.ZeroTempMutableCollection:
            {
                RewriteCount++;
                // T4 interface plans carry the selected List<T> here; T3
                // plans carry the converted concrete type itself.
                var replacement = CollectionExprEmitter.BuildZeroTempCollection(
                    plan.EffectiveConstructedType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));
                CountEmittedInitializerNodes(replacement, visited);
                return replacement;
            }

            case CollectionExprOutcome.ZeroTempBuilderFactory:
            {
                RewriteCount++;
                var replacement = CollectionExprEmitter.BuildBuilderFactory(
                    plan.BuilderFactory!,
                    plan.TargetElementType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));
                CountEmittedInitializerNodes(replacement, visited);
                return replacement;
            }

            case CollectionExprOutcome.ZeroTempConstructibleCollection:
            {
                RewriteCount++;
                var replacement = CollectionExprEmitter.BuildConstructibleCollection(
                    plan.EffectiveConstructedType!,
                    plan.TargetElementType,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));
                CountEmittedInitializerNodes(replacement, visited);
                return replacement;
            }

            case CollectionExprOutcome.StatementSequence:
                return LowerStatement(plan, visited);

            default:
                // Defensive: no lowering decision taken. Treat as an explained
                // skip; the residual gate stays truthful.
                return visited.WithAdditionalAnnotations(
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }
    }

    /// <summary>
    /// W-17②/G-41 (upgrade plan §6.4-R0): C24's whole-tree gate counts every
    /// on-disk initializer node of the three residual kinds, but C24 analyzes
    /// BEFORE this rule rewrites on the shared progressive tree — so the
    /// initializer syntax C36 EMITS (<c>new C&lt;T&gt;(){ … }</c> wrappers)
    /// was silently unpaired and failed the chain as "residual without
    /// accounting" (the rt10 JsonSchemaExporter/PropertyRefCacheBuilder
    /// CS2J0545 aborts). Count the NET-NEW residual nodes the replacement
    /// introduces over the visited node: already-rewritten elements carry
    /// their own counts (inner emissions were counted at their replacement;
    /// original initializer-bearing creations are deferred-paired by C24), so
    /// the diff isolates exactly the wrapper this emission adds. Array forms
    /// contribute zero (the ArrayInitializer kind is excluded from C24's
    /// residual kinds).
    /// </summary>
    private void CountEmittedInitializerNodes(SyntaxNode replacement, SyntaxNode visited)
    {
        var netNew = InitializerSyntaxFacts.CountResidualNodes(replacement)
                     - InitializerSyntaxFacts.CountResidualNodes(visited);
        if (netNew > 0)
            _context.Statistics.Initializers.EmittedByCollectionExprNodes += netNew;
    }

    /// <summary>
    /// CE-7: allocates <c>__ed_n</c>, builds the Add/AddRange/foreach sequence
    /// (spread sources inlined once, strict source order), registers it at the
    /// anchor and returns the temp reference (<c>ToArray()</c> for array
    /// targets). A missing anchor is a defensive 0583 preserve.
    /// </summary>
    private SyntaxNode LowerStatement(
        CollectionExprPlan plan,
        CollectionExpressionSyntax visited)
    {
        var anchor = ExpressionDesugarTempLifter.FindAnchor(plan.Node);
        var stats = _context.Statistics.CollectionExpr;

        if (anchor == null)
        {
            // Analyze proved liftability; reaching here means a structural gap.
            var strict = _context.Options.CollectionExpr.Strict;
            _context.Report(
                CollectionExprDiagnostics.ResolveSeverity(
                    CollectionExprDiagnosticCodes.CS2J0583, strict),
                CollectionExprDiagnosticCodes.CS2J0583,
                string.Format(
                    CollectionExprDiagnostics.NotLiftableMessage,
                    "a position with no statement anchor"),
                plan.Node);
            stats.AddSkip(CollectionExprDiagnosticCodes.CS2J0583);

            // M3 whole-tree gate pairing: preserved statement site (1, via the
            // skip) plus the direct spread nodes it keeps on disk.
            stats.SkippedSpreadElements += plan.Node.Elements
                .Count(CollectionExprSyntaxFacts.IsSpread);

            return visited.WithAdditionalAnnotations(
                ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }

        var emission = CollectionExprEmitter.BuildStatementSequence(
            _context.SemanticModel!,
            plan,
            visited.Elements);

        // W-17②/G-41: the lifted <c>var __ed_n = new List<T>(){}</c> temp
        // declaration emits residual initializer syntax C24's analyze-time
        // pairing cannot see (C24 runs first); report it for the whole-tree
        // gate. Element expressions carried into the Add statements were
        // either already counted (inner emissions) or deferred-paired by C24
        // (original initializer-bearing creations), so only the temp
        // declaration's own nodes are reported here.
        if (emission.EmittedInitializerNodes > 0)
            _context.Statistics.Initializers.EmittedByCollectionExprNodes +=
                emission.EmittedInitializerNodes;

        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(
            emission.Statements, anchor, plan.LiftOrder));

        foreach (var expansion in emission.Expansions)
        {
            switch (expansion)
            {
                case SpreadExpansion.AddRange:
                    stats.SpreadAddRange++;
                    break;
                case SpreadExpansion.SpanForeach:
                    stats.SpreadSpanForeach++;
                    break;
                default:
                    stats.SpreadEnumerableForeach++;
                    break;
            }
        }

        stats.StatementLiftedSites++;
        stats.TempsLifted++;
        RewriteCount++;
        return emission.Reference;
    }
}
