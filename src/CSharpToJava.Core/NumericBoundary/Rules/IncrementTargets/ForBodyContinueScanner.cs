using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// Reports a <c>continue</c> that targets the loop directly enclosing the
/// scanned body (jumps nested inside an inner loop, switch section lambda or
/// local function do not count — they never reach the outer loop).
/// <para>
/// The for-incrementor linearization moves the discarded write to the end of
/// the loop body: a direct <c>continue</c> would skip that write, so such
/// bodies are refused.
/// </para>
/// </summary>
internal sealed class ForBodyContinueScanner : CSharpSyntaxWalker
{
    private int _loopDepth;
    public bool HasDirectContinue { get; private set; }

    public static bool Scan(StatementSyntax body)
    {
        var scanner = new ForBodyContinueScanner();
        body.Accept(scanner);
        return scanner.HasDirectContinue;
    }

    // Anonymous-function and local-function bodies have their own continue
    // scope (illegal there without an enclosing loop of their own).
    public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
    public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
    public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }
    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) { }

    public override void VisitDoStatement(DoStatementSyntax node) => EnterLoop(node);
    public override void VisitWhileStatement(WhileStatementSyntax node) => EnterLoop(node);
    public override void VisitForStatement(ForStatementSyntax node) => EnterLoop(node);
    public override void VisitForEachStatement(ForEachStatementSyntax node) => EnterLoop(node);
    public override void VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => EnterLoop(node);

    public override void VisitContinueStatement(ContinueStatementSyntax node)
    {
        if (_loopDepth == 0)
        {
            HasDirectContinue = true;
        }

        base.VisitContinueStatement(node);
    }

    private void EnterLoop<T>(T node) where T : StatementSyntax
    {
        _loopDepth++;
        DefaultVisit(node);
        _loopDepth--;
    }
}
