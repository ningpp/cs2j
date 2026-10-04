using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Bottom-up rewriter for C35 (design §4.1 / NC-1...). Chain-root conditional
/// accesses are replaced as whole plans:
/// <list type="bullet">
/// <item><see cref="NullConditionalLoweringShape.Inline"/>: zero-declaration
/// ternary returned directly;</item>
/// <item><see cref="NullConditionalLoweringShape.TempTernary"/>: segment
/// declarations go to the shared family TempLifter as pending entries, the
/// ternary references the temp;</item>
/// <item><see cref="NullConditionalLoweringShape.GuardedBlock"/> (multi
/// segment, value) and <see cref="NullConditionalLoweringShape.StatementIf"/>
/// (void statement): the whole anchor statement is replaced via this
/// directory's <see cref="PendingAnchorReplacement"/> capability.</item>
/// </list>
/// Sites that must stay (defensive skips, C51 deferrals, non-strict warnings)
/// keep the node and receive the explained annotation so the residual gate
/// pairs them.
/// </summary>
internal sealed class NullConditionalRewriter : CSharpSyntaxRewriter
{
    private readonly NullConditionalAnalysis _analysis;
    private readonly HashSet<SyntaxNode> _preservedRoots = new();
    private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _replacementMarkers = new();

    public NullConditionalRewriter(
        NullConditionalAnalysis analysis,
        SemanticModel model)
    {
        _analysis = analysis;

        foreach (var plan in analysis.Plans)
        {
            if (plan.Outcome == ChainOutcome.Preserved)
                _preservedRoots.Add(plan.RootOriginal);
        }
    }

    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Original anchor nodes -> family-lifter annotations.</summary>
    public Dictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; } = new();

    /// <summary>Anchor statement replacements, applied inside this rule's Rewrite.</summary>
    public List<PendingAnchorReplacement> AnchorReplacements { get; } = new();

    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;

        if (!_analysis.TryGetPlan(node, out var plan))
            return visited;

        if (plan.Outcome == ChainOutcome.Preserved)
            return visited;

        // Walk the visited dependent chain: original CA -> visited CA.
        var visitedCaByOriginal = new Dictionary<
            ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax>();
        var currentOriginal = node;
        var currentVisited = visited;
        ExpressionSyntax terminalVisited;
        while (true)
        {
            visitedCaByOriginal[currentOriginal] = currentVisited;
            if (currentVisited.WhenNotNull is ConditionalAccessExpressionSyntax innerVisited)
            {
                currentOriginal = (ConditionalAccessExpressionSyntax)currentOriginal.WhenNotNull;
                currentVisited = innerVisited;
                continue;
            }

            terminalVisited = currentVisited.WhenNotNull;
            break;
        }

        switch (plan.Shape)
        {
            case NullConditionalLoweringShape.Inline:
            {
                var consumption = NullConditionalEmitter.BuildSingleSegmentTernary(
                    terminalVisited, visited.Expression,
                    plan.Segments[0].ReceiverType, plan);
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
                    terminalVisited, receiver,
                    plan.Segments[0].ReceiverType, plan);
                RewriteCount++;
                return SyntaxFactory.ParenthesizedExpression(consumption);
            }

            case NullConditionalLoweringShape.StatementIf:
            {
                var deepest = plan.Segments[^1].TempName;
                var terminalStatement = NullConditionalEmitter.BuildVoidTerminalStatement(
                    terminalVisited, deepest, plan.Segments[^1].ReceiverType);

                var statements = NullConditionalEmitter.BuildMultiSegmentPrefix(
                    plan, visitedCaByOriginal, new[] { terminalStatement });

                RegisterAnchorReplacement(plan, statements);
                RewriteCount++;
                return visited;
            }

            case NullConditionalLoweringShape.GuardedBlock:
            {
                var prefix = NullConditionalEmitter.BuildMultiSegmentPrefix(
                    plan, visitedCaByOriginal, Array.Empty<StatementSyntax>());
                var nestedTernary = NullConditionalEmitter.BuildNestedTernary(
                    plan, terminalVisited);

                var finalStatement = RebuildValueAnchorStatement(plan, nestedTernary);
                var statements = prefix;
                statements.Add(finalStatement);

                RegisterAnchorReplacement(plan, statements);
                RewriteCount++;
                return visited;
            }

            default:
                return visited;
        }
    }

    /// <summary>
    /// Rebuilds the original value anchor statement with the nested ternary
    /// in place of the conditional access: keeps a local declaration's
    /// type/name; for a return statement preserves any wrapping expression
    /// (e.g. <c>return Sink(a?.X);</c>) by substituting the CA node only.
    /// </summary>
    private static StatementSyntax RebuildValueAnchorStatement(
        ChainPlan plan, ExpressionSyntax nestedTernary)
    {
        if (plan.Anchor is LocalDeclarationStatementSyntax localOriginal)
        {
            var declaration = localOriginal.Declaration;
            var first = declaration.Variables[0]
                .WithInitializer(SyntaxFactory.EqualsValueClause(nestedTernary));
            var variables = declaration.Variables.Replace(
                declaration.Variables[0], first);
            return localOriginal.WithDeclaration(
                declaration.WithVariables(variables));
        }

        if (plan.Anchor is ReturnStatementSyntax returnOriginal)
        {
            var expression = returnOriginal.Expression!
                .ReplaceNode(plan.RootOriginal, nestedTernary);
            return SyntaxFactory.ReturnStatement(expression);
        }

        return SyntaxFactory.ReturnStatement(nestedTernary);
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

    private void RegisterAnchorReplacement(
        ChainPlan plan, IReadOnlyList<StatementSyntax> statements)
    {
        var anchor = plan.Anchor!;

        // An independent inner root (e.g. j?[i] inside j?[i]?.Length) may
        // have anchored pending temps to this same statement. The splice
        // removes the anchor node, so carry the family anchor annotation
        // onto the first replacement statement to keep insertion working.
        if (AnchorAnnotations.TryGetValue(anchor, out var anchorAnnotation))
        {
            var copied = new List<StatementSyntax>(statements.Count)
            {
                statements[0].WithAdditionalAnnotations(anchorAnnotation),
            };
            for (var i = 1; i < statements.Count; i++)
                copied.Add(statements[i]);
            statements = copied;
        }

        var marker = new SyntaxAnnotation("ed-nc-anchor-replacement");
        AnchorReplacements.Add(new PendingAnchorReplacement(
            anchor, marker, statements));
        _replacementMarkers[anchor] = marker;
    }

    /// <summary>
    /// Attaches explained annotations to preserved roots, family anchor
    /// annotations and replacement markers to anchor statements (same
    /// discipline as the C24/C36 rewriters) so they survive the rewrites.
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

            if (_replacementMarkers.TryGetValue(node, out var replacementMarker))
                result = result.WithAdditionalAnnotations(replacementMarker);
        }

        return result;
    }
}
