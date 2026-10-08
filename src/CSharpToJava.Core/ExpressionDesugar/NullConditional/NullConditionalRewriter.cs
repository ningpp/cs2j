using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar.NullConditional.Assignment;

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
    private readonly RuleSiteMarkers<ChainPlan> _markers;
    private readonly IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations;
    private readonly IReadOnlyDictionary<ChainPlan, SyntaxAnnotation> _anchorReplacementsByPlan;

    public NullConditionalRewriter(
        NullConditionalAnalysis analysis,
        SemanticModel model,
        RuleSiteMarkers<ChainPlan> markers,
        IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> anchorAnnotations,
        IReadOnlyDictionary<ChainPlan, SyntaxAnnotation> anchorReplacementsByPlan)
    {
        _analysis = analysis;
        _markers = markers;
        _anchorAnnotations = anchorAnnotations;
        _anchorReplacementsByPlan = anchorReplacementsByPlan;
    }

    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Original anchor nodes -> family-lifter annotations (pre-stamped).</summary>
    public IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
        => _anchorAnnotations;

    /// <summary>Anchor statement replacements, applied inside this rule's Rewrite.</summary>
    public List<PendingAnchorReplacement> AnchorReplacements { get; } = new();

    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;

        var plan = _markers.TryResolve(visited)
            ?? (_analysis.TryGetPlan(node, out var originalPlan) ? originalPlan : null);
        if (plan == null)
            return visited;

        if (plan.Outcome == ChainOutcome.Preserved)
        {
            // Pair the residual gate directly (the site marker is how the
            // plan was resolved after earlier rules refreshed node identity).
            return visited.WithAdditionalAnnotations(
                ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }

        // C51 assignment terminals render through the Assignment sub-rule
        // (NA-1...NA-7); read-side terminals keep the flow below.
        if (plan.Terminal.Kind == ChainTerminalKind.Assign)
            return RenderAssignment(visited, plan);

        // Walk the visited dependent chain. Keys MUST be the analysis-time
        // original CA nodes (plan.Segments[k].Ca): after the family stamping
        // pass every tree instance is fresh, so Visit's `node` never equals
        // the plan's original nodes. The visited CA skeleton still mirrors the
        // analyzed chain 1:1 (earlier rules only rewrite inside expressions).
        var visitedCaByOriginal = MapVisitedChain(plan, visited, out var terminalVisited);

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
    /// Maps each analyzed segment's ORIGINAL CA node to its counterpart in the
    /// visited (post-earlier-rules) tree, descending the visited CA skeleton,
    /// and returns the innermost terminal expression. Keying on analysis nodes
    /// is required because the family stamping pass refreshes every instance.
    /// </summary>
    private static Dictionary<
        ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax>
        MapVisitedChain(
            ChainPlan plan,
            ConditionalAccessExpressionSyntax visitedOuter,
            out ExpressionSyntax terminalVisited)
    {
        var map = new Dictionary<
            ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax>();
        var current = visitedOuter;
        var count = plan.Segments.Count;
        for (var k = 0; k < count; k++)
        {
            map[plan.Segments[k].Ca] = current;
            if (k < count - 1)
                current = (ConditionalAccessExpressionSyntax)current.WhenNotNull;
        }

        terminalVisited = current.WhenNotNull;
        return map;
    }

    /// <summary>
    /// C51 rendering for one lowered assignment-terminal plan (design §4.2/
    /// §4.3): value contexts become null ternaries containing the assignment
    /// in the true branch (NA-5, zero result temp); statement contexts become
    /// a guarded block or, for a single whitelisted receiver, a bare
    /// <c>if (a != null) a.b = c;</c> (NA-1/2/4). The RHS and index argument
    /// nodes never leave the guarded/true-branch subtree.
    /// </summary>
    private SyntaxNode RenderAssignment(
        ConditionalAccessExpressionSyntax visited,
        ChainPlan plan)
    {
        // Keys come from plan.Segments (analysis originals); see the read-side
        // walk above for why Visit's `node` instances cannot be used as keys.
        var visitedCaByOriginal = MapVisitedChain(
            plan, visited, out var terminalVisitedExpr);
        var terminalAssignment = (AssignmentExpressionSyntax)terminalVisitedExpr;

        if (plan.IsCoalesceAssignment)
            return RenderCoalesceAssignment(
                visited, plan, visitedCaByOriginal, terminalAssignment);

        switch (plan.Shape)
        {
            case NullConditionalLoweringShape.Inline:
            {
                var replaced = NullConditionalAssignmentEmitter.ReplaceAssignmentLeft(
                    terminalAssignment, visited.Expression);
                var ternary = NullConditionalAssignmentEmitter.BuildSingleValueTernary(
                    replaced, visited.Expression, plan);
                RewriteCount++;
                return SyntaxFactory.ParenthesizedExpression(ternary);
            }

            case NullConditionalLoweringShape.TempTernary:
            {
                var segmentName = plan.Segments[0].TempName;
                RegisterPending(
                    plan,
                    ExpressionDesugarTempLifter.DeclareVar(
                        segmentName, visited.Expression));

                var receiver = SyntaxFactory.IdentifierName(segmentName);
                var replaced = NullConditionalAssignmentEmitter.ReplaceAssignmentLeft(
                    terminalAssignment, receiver);
                var ternary = NullConditionalAssignmentEmitter.BuildSingleValueTernary(
                    replaced, receiver, plan);
                RewriteCount++;
                return SyntaxFactory.ParenthesizedExpression(ternary);
            }

            case NullConditionalLoweringShape.StatementIf:
            {
                List<StatementSyntax> statements;

                if (plan.Segments.Count == 1
                    && plan.Segments[0].TempName.Length == 0)
                {
                    // Single whitelisted receiver: zero declaration.
                    var replaced = NullConditionalAssignmentEmitter.ReplaceAssignmentLeft(
                        terminalAssignment, visited.Expression);
                    var condition = SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        visited.Expression,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    statements = new List<StatementSyntax>
                    {
                        SyntaxFactory.IfStatement(
                            condition,
                            SyntaxFactory.Block(
                                SyntaxFactory.ExpressionStatement(replaced))),
                    };
                }
                else
                {
                    var deepestReceiver = SyntaxFactory.IdentifierName(
                        plan.Segments[^1].TempName);
                    var replaced = NullConditionalAssignmentEmitter.ReplaceAssignmentLeft(
                        terminalAssignment, deepestReceiver);
                    var innermost = new StatementSyntax[]
                    {
                        SyntaxFactory.ExpressionStatement(replaced),
                    };
                    statements = NullConditionalEmitter.BuildMultiSegmentPrefix(
                        plan, visitedCaByOriginal, innermost);
                }

                RegisterAnchorReplacement(plan, statements);
                RewriteCount++;
                return visited;
            }

            case NullConditionalLoweringShape.GuardedBlock:
            {
                var prefix = NullConditionalEmitter.BuildMultiSegmentPrefix(
                    plan, visitedCaByOriginal, Array.Empty<StatementSyntax>());
                var nestedTernary = NullConditionalAssignmentEmitter.BuildNestedValueTernary(
                    plan, terminalAssignment);
                prefix.Add(RebuildValueAnchorStatement(plan, nestedTernary));

                RegisterAnchorReplacement(plan, prefix);
                RewriteCount++;
                return visited;
            }

            default:
                return visited;
        }
    }

    /// <summary>
    /// NA-6 rendering (design §3.5): the coalesced member is read once into
    /// <c>__ed_nN</c> and the assignment happens only inside an
    /// <c>if (nN == null)</c>; statement form nests that body in the receiver
    /// guard, value form prefixes the guarded member read and finishes with
    /// the <c>s != null ? n == null ? s.b = c : n : null</c> ternary.
    /// </summary>
    private SyntaxNode RenderCoalesceAssignment(
        ConditionalAccessExpressionSyntax visited,
        ChainPlan plan,
        IReadOnlyDictionary<ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax> visitedCaByOriginal,
        AssignmentExpressionSyntax terminalAssignment)
    {
        List<StatementSyntax> statements;

        if (plan.Shape == NullConditionalLoweringShape.StatementIf)
        {
            if (plan.Segments.Count == 1
                && plan.Segments[0].TempName.Length == 0)
            {
                // Single whitelisted receiver: zero segment temp.
                var receiver = visited.Expression;
                var deepest = NullConditionalAssignmentEmitter.BuildCoalesceDeepestBlock(
                    plan, terminalAssignment, receiver);
                var outerIf = SyntaxFactory.IfStatement(
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        receiver,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                    SyntaxFactory.Block(deepest));
                statements = new List<StatementSyntax> { outerIf };
            }
            else if (plan.Segments.Count == 1)
            {
                // Single non-whitelist receiver: one segment temp.
                var segmentName = plan.Segments[0].TempName;
                statements = new List<StatementSyntax>
                {
                    ExpressionDesugarTempLifter.DeclareVar(segmentName, visited.Expression),
                };

                var segmentReceiver = SyntaxFactory.IdentifierName(segmentName);
                var deepest = NullConditionalAssignmentEmitter.BuildCoalesceDeepestBlock(
                    plan, terminalAssignment, segmentReceiver);
                statements.Add(SyntaxFactory.IfStatement(
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        segmentReceiver,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                    SyntaxFactory.Block(deepest)));
            }
            else
            {
                // Multi-segment: deepest block (var nN = sN.b; if ...) inside
                // the read-side prefix guard chain.
                var deepestReceiver = SyntaxFactory.IdentifierName(
                    plan.Segments[^1].TempName);
                var innermost = NullConditionalAssignmentEmitter.BuildCoalesceDeepestBlock(
                    plan, terminalAssignment, deepestReceiver);
                statements = NullConditionalEmitter.BuildMultiSegmentPrefix(
                    plan, visitedCaByOriginal, innermost);
            }
        }
        else
        {
            // GuardedBlock (value form): prefetch chain, inner-temp null
            // declaration + guarded read, then the value ternary statement.
            var deepestReceiver = SyntaxFactory.IdentifierName(
                plan.Segments[^1].TempName);
            var innermost = new[]
            {
                NullConditionalAssignmentEmitter.BuildInnerTempAssignment(
                    plan.InnerTempName,
                    terminalAssignment.Left,
                    deepestReceiver),
            };

            statements = NullConditionalEmitter.BuildMultiSegmentPrefix(
                plan, visitedCaByOriginal, innermost);

            // Inner temp declaration must precede the prefetch if chain
            // (last statement emitted by the prefix).
            var innerNull = NullConditionalAssignmentEmitter.DeclareInnerNull(
                plan.InnerTempName, plan.TerminalValueType);
            statements.Insert(statements.Count - 1, innerNull);

            var valueTernary = NullConditionalAssignmentEmitter.BuildCoalesceValueTernary(
                plan, terminalAssignment);
            statements.Add(RebuildValueAnchorStatement(plan, valueTernary));
        }

        RegisterAnchorReplacement(plan, statements);
        RewriteCount++;
        return visited;
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

        // The anchor's family-lift annotation was pre-stamped in the plan.
        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(
            new[] { statement }, anchor, plan.RootOriginal.SpanStart));
    }

    private void RegisterAnchorReplacement(
        ChainPlan plan, IReadOnlyList<StatementSyntax> statements)
    {
        var anchor = plan.Anchor!;

        // An independent inner root (e.g. j?[i] inside j?[i]?.Length) may
        // have anchored pending temps to this same statement. The splice
        // removes the anchor node, so carry the family anchor annotation
        // onto the first replacement statement to keep insertion working.
        if (_anchorAnnotations.TryGetValue(anchor, out var anchorAnnotation))
        {
            var copied = new List<StatementSyntax>(statements.Count)
            {
                statements[0].WithAdditionalAnnotations(anchorAnnotation),
            };
            for (var i = 1; i < statements.Count; i++)
                copied.Add(statements[i]);
            statements = copied;
        }

        var marker = _anchorReplacementsByPlan[plan];
        AnchorReplacements.Add(new PendingAnchorReplacement(
            anchor, marker, statements));
    }
}
