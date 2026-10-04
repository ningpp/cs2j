using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Bottom-up rewriter for C35 (design §4.1 / NC-1...). Chain-root conditional
/// accesses are replaced as whole plans: value shapes become null-check
/// ternaries; segment declarations go to the shared family TempLifter as
/// pending entries; void/guarded statement shapes are handled via
/// <see cref="PendingAnchorReplacement"/> (later milestones). Sites that must
/// stay (defensive skips, C51 deferrals, non-strict warnings) keep the node
/// and receive the explained annotation so the residual gate pairs them.
/// </summary>
internal sealed class NullConditionalRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly NullConditionalAnalysis _analysis;
    private readonly HashSet<SyntaxNode> _preservedRoots = new();

    public NullConditionalRewriter(
        NullConditionalAnalysis analysis,
        SemanticModel model)
    {
        _analysis = analysis;
        _model = model;

        foreach (var plan in analysis.Plans)
        {
            if (plan.Outcome == ChainOutcome.Preserved)
                _preservedRoots.Add(plan.RootOriginal);
        }
    }

    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Original anchor nodes -> annotations, consumed by the family TempLifter.</summary>
    public Dictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; } = new();

    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;

        if (!_analysis.TryGetPlan(node, out var plan))
            return visited;

        if (plan.Outcome == ChainOutcome.Preserved)
            return visited;

        // Single-segment shapes only (M1). Multi-segment lowering lands later.
        var terminalVisited = GetTerminal(visited);

        switch (plan.Shape)
        {
            case NullConditionalLoweringShape.Inline:
            {
                var consumption = NullConditionalEmitter.BuildSingleSegmentTernary(
                    terminalVisited, visited.Expression, plan);
                RewriteCount++;
                return SyntaxFactory.ParenthesizedExpression(consumption);
            }

            case NullConditionalLoweringShape.TempTernary:
            {
                var segmentName = plan.Segments[0].TempName;
                RegisterPending(
                    plan,
                    ExpressionDesugarTempLifter.DeclareVar(
                        segmentName, visited.Expression));

                var receiver = SyntaxFactory.IdentifierName(segmentName);
                var consumption = NullConditionalEmitter.BuildSingleSegmentTernary(
                    terminalVisited, receiver, plan);
                RewriteCount++;
                return SyntaxFactory.ParenthesizedExpression(consumption);
            }

            default:
                return visited;
        }
    }

    /// <summary>Navigates the visited dependent-CA chain to the innermost terminal.</summary>
    private static ExpressionSyntax GetTerminal(ConditionalAccessExpressionSyntax visitedRoot)
    {
        var current = visitedRoot;
        while (current.WhenNotNull is ConditionalAccessExpressionSyntax inner)
            current = inner;
        return current.WhenNotNull;
    }

    private void RegisterPending(ChainPlan plan, StatementSyntax statement)
    {
        var anchor = plan.Anchor;
        if (anchor == null)
            return;

        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(
            new[] { statement }, anchor, plan.RootOriginal.SpanStart));

        if (!AnchorAnnotations.ContainsKey(anchor))
            AnchorAnnotations[anchor] = new SyntaxAnnotation(
                ExpressionDesugarTempLifter.AnchorAnnotationKind);
    }

    /// <summary>
    /// Attaches explained annotations to preserved roots and anchor
    /// annotations to anchor statements (same discipline as the C24/C36
    /// rewriters) so they survive the tree rewrites.
    /// </summary>
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var result = base.Visit(node);

        if (node != null && result != null)
        {
            if (_preservedRoots.Contains(node))
            {
                result = result.WithAdditionalAnnotations(
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
            }

            if (AnchorAnnotations.TryGetValue(node, out var anchorAnnotation))
                result = result.WithAdditionalAnnotations(anchorAnnotation);
        }

        return result;
    }
}
