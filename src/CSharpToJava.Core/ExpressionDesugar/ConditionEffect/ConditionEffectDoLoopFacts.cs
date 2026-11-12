using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// <c>do</c>-slice facts (JE-04 / G-36). The canonical form evaluates the
/// condition once per completed iteration, so a <c>continue</c> bound to the
/// lifted loop must evaluate the condition before starting the next iteration
/// (a plain <c>continue</c> would skip the tail check). Nested loops, lambdas,
/// anonymous methods and local functions keep their own <c>continue</c>.
///
/// <para>
/// A <c>continue</c> inside a <c>switch</c> section still binds the outer loop,
/// but the replacement's exit statement cannot be <c>break</c>: inside a
/// section <c>break</c> binds the <c>switch</c>, not the loop. Those sites use
/// <c>goto</c> to a label emitted right after the lifted <c>while</c>.
/// </para>
/// </summary>
internal static class ConditionEffectDoLoopFacts
{
    /// <summary>
    /// One entry per <c>continue</c> bound to the lifted <c>do</c>, in document
    /// order; the value tells whether the site sits inside a <c>switch</c>
    /// section (and therefore needs the <c>goto</c> exit).
    /// </summary>
    public static List<bool> CollectContinueSwitchCapture(StatementSyntax body)
    {
        var result = new List<bool>();
        Walk(body, insideSwitch: false, result);
        return result;
    }

    /// <summary>
    /// Replaces every <c>continue</c> of <paramref name="body"/> that binds to
    /// the lifted loop with <c>{ bool m = C; if (!m) break|goto; continue; }</c>.
    /// The condition is duplicated per site; only one path runs at a time, so
    /// its side effects are not repeated.
    /// </summary>
    public static StatementSyntax RewriteContinues(
        StatementSyntax body,
        ExpressionSyntax condition,
        IReadOnlyList<ConditionEffectContinueSite> sites,
        string endLabelName)
        => (StatementSyntax)new ContinueRewriter(
            condition, sites, endLabelName).Visit(body)!;

    private static void Walk(SyntaxNode node, bool insideSwitch, List<bool> result)
    {
        if (node is ContinueStatementSyntax)
        {
            result.Add(insideSwitch);
            return;
        }

        if (StopsContinueRewrite(node))
            return;

        var inner = insideSwitch || node is SwitchStatementSyntax;
        foreach (var child in node.ChildNodes())
            Walk(child, inner, result);
    }

    /// <summary>
    /// Constructs that own their own <c>continue</c> (or defer it). Not
    /// descended into by either the collector or the rewriter.
    /// </summary>
    private static bool StopsContinueRewrite(SyntaxNode node)
        => node is ForStatementSyntax or ForEachStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax
            or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax;

    private sealed class ContinueRewriter : CSharpSyntaxRewriter
    {
        private readonly ExpressionSyntax _condition;
        private readonly IReadOnlyList<ConditionEffectContinueSite> _sites;
        private readonly string _endLabelName;
        private int _index;

        public ContinueRewriter(
            ExpressionSyntax condition,
            IReadOnlyList<ConditionEffectContinueSite> sites,
            string endLabelName)
        {
            _condition = condition;
            _sites = sites;
            _endLabelName = endLabelName;
        }

        public override SyntaxNode? VisitContinueStatement(
            ContinueStatementSyntax node)
        {
            var site = _sites[_index];
            _index++;

            StatementSyntax exit = site.NeedsGoto
                ? SyntaxFactory.GotoStatement(
                    SyntaxKind.GotoStatement,
                    SyntaxFactory.IdentifierName(_endLabelName))
                : SyntaxFactory.BreakStatement();

            var statements = new List<StatementSyntax>(3)
            {
                ConditionEffectRewriter.DeclareCondition(
                    site.TempName, _condition),
                SyntaxFactory.IfStatement(
                    SyntaxFactory.PrefixUnaryExpression(
                        SyntaxKind.LogicalNotExpression,
                        SyntaxFactory.IdentifierName(site.TempName)),
                    exit),
                SyntaxFactory.ContinueStatement()
                    .WithTrailingTrivia(node.GetTrailingTrivia()),
            };

            return SyntaxFactory.Block(SyntaxFactory.List(statements))
                .WithLeadingTrivia(node.GetLeadingTrivia());
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            => node;

        public override SyntaxNode? VisitForEachStatement(
            ForEachStatementSyntax node)
            => node;

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
            => node;

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
            => node;

        public override SyntaxNode? VisitLocalFunctionStatement(
            LocalFunctionStatementSyntax node)
            => node;

        public override SyntaxNode? VisitSimpleLambdaExpression(
            SimpleLambdaExpressionSyntax node)
            => node;

        public override SyntaxNode? VisitParenthesizedLambdaExpression(
            ParenthesizedLambdaExpressionSyntax node)
            => node;

        public override SyntaxNode? VisitAnonymousMethodExpression(
            AnonymousMethodExpressionSyntax node)
            => node;
    }
}

/// <summary>One <c>continue</c> site of a lifted <c>do</c> loop.</summary>
internal sealed class ConditionEffectContinueSite
{
    /// <summary>File-unique condition temp for this site.</summary>
    public required string TempName { get; init; }

    /// <summary>True when the site sits in a <c>switch</c> section (exit by <c>goto</c>).</summary>
    public required bool NeedsGoto { get; init; }
}
