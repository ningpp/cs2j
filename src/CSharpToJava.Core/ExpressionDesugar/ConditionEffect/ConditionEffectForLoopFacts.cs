using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// <c>for</c>-slice helpers (JE-04 / G-36). The header is split into
/// statements so the canonical form can run the initializer before the
/// <c>while (true)</c> and the incrementors once per completed iteration.
/// A <c>continue</c> that binds to the lifted loop must execute the
/// incrementors before re-testing the condition; nested loops, lambdas,
/// anonymous methods and local functions keep their own <c>continue</c>.
/// </summary>
internal static class ConditionEffectForLoopFacts
{
    /// <summary>
    /// INIT as statements: a declaration stays one local declaration (all
    /// declarators in source order), an expression list is split into one
    /// expression statement per comma-separated item, an absent initializer
    /// emits nothing.
    /// </summary>
    public static List<StatementSyntax> BuildInitializerStatements(
        ForStatementSyntax node)
    {
        var statements = new List<StatementSyntax>();
        if (node.Declaration is { } declaration)
        {
            statements.Add(SyntaxFactory.LocalDeclarationStatement(declaration));
            return statements;
        }

        foreach (var initializer in node.Initializers)
            statements.Add(SyntaxFactory.ExpressionStatement(initializer));

        return statements;
    }

    /// <summary>INCR as statements, one per comma-separated expression.</summary>
    public static List<StatementSyntax> BuildIncrementStatements(
        ForStatementSyntax node)
    {
        var statements = new List<StatementSyntax>(node.Incrementors.Count);
        foreach (var incrementor in node.Incrementors)
            statements.Add(SyntaxFactory.ExpressionStatement(incrementor));
        return statements;
    }

    /// <summary>
    /// Rewrites every <c>continue</c> of <paramref name="body"/> that binds to
    /// the lifted loop into <c>{ INCR...; continue; }</c>. <c>break</c> is never
    /// touched: a loop <c>break</c> still exits the new <c>while</c>, a
    /// <c>switch</c> <c>break</c> still exits its section.
    /// </summary>
    public static StatementSyntax RewriteContinues(
        StatementSyntax body, List<StatementSyntax> increments)
        => (StatementSyntax)new ContinueRewriter(increments).Visit(body)!;

    /// <summary>
    /// True when the (already rewritten) body cannot fall through to the end of
    /// the loop body; the trailing incrementor statements would then be
    /// unreachable (CS0162) and unnecessary, because every exiting path either
    /// ran the incrementors before its <c>continue</c> or left the loop.
    /// </summary>
    public static bool EndsWithUnconditionalTransfer(StatementSyntax statement)
        => statement switch
        {
            BlockSyntax block => block.Statements.Count > 0
                                 && EndsWithUnconditionalTransfer(block.Statements[^1]),
            ContinueStatementSyntax or BreakStatementSyntax
                or ReturnStatementSyntax or ThrowStatementSyntax => true,
            IfStatementSyntax { Else: { } elseClause } ifStatement
                => EndsWithUnconditionalTransfer(ifStatement.Statement)
                   && EndsWithUnconditionalTransfer(elseClause.Statement),
            CheckedStatementSyntax checkedStatement
                => EndsWithUnconditionalTransfer(checkedStatement.Block),
            _ => false,
        };

    /// <summary>
    /// True when a <c>continue</c> bound to this loop is nested inside a
    /// <c>try</c>/<c>using</c>/<c>lock</c>/<c>checked</c> statement. Running
    /// the incrementors before such a <c>continue</c> (instead of after the
    /// finally/dispose/exit as the original <c>for</c> did) is observable, so
    /// the site is preserved with a Warning.
    /// </summary>
    public static bool ContainsContinueInCapturedContext(StatementSyntax body)
        => AnyCapturedContinue(body, captured: false);

    private static bool AnyCapturedContinue(SyntaxNode node, bool captured)
    {
        if (node is ContinueStatementSyntax)
            return captured;

        if (node is ForStatementSyntax or ForEachStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax
            or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
        {
            return false;
        }

        var innerCaptured = captured
            || node is TryStatementSyntax or UsingStatementSyntax
                or LockStatementSyntax or CheckedStatementSyntax;

        foreach (var child in node.ChildNodes())
        {
            if (AnyCapturedContinue(child, innerCaptured))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Replaces <c>continue;</c> with <c>{ INCR...; continue; }</c> and stops at
    /// every construct that owns its own <c>continue</c>.
    /// </summary>
    private sealed class ContinueRewriter : CSharpSyntaxRewriter
    {
        private readonly List<StatementSyntax> _increments;

        public ContinueRewriter(List<StatementSyntax> increments)
            => _increments = increments;

        public override SyntaxNode? VisitContinueStatement(
            ContinueStatementSyntax node)
        {
            var statements = new List<StatementSyntax>(_increments.Count + 1);
            statements.AddRange(_increments);
            statements.Add(SyntaxFactory.ContinueStatement()
                .WithTrailingTrivia(node.GetTrailingTrivia()));

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
