using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

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
                RewriteCount++;
                return CollectionExprEmitter.BuildZeroTempArray(
                    plan.TargetElementType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));

            case CollectionExprOutcome.ZeroTempMutableCollection:
                RewriteCount++;
                // T4 interface plans carry the selected List<T> here; T3
                // plans carry the converted concrete type itself.
                return CollectionExprEmitter.BuildZeroTempCollection(
                    plan.EffectiveConstructedType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));

            case CollectionExprOutcome.ZeroTempBuilderFactory:
                RewriteCount++;
                return CollectionExprEmitter.BuildBuilderFactory(
                    plan.BuilderFactory!,
                    plan.TargetElementType!,
                    CollectionExprEmitter.ExtractExpressions(visited.Elements));

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
