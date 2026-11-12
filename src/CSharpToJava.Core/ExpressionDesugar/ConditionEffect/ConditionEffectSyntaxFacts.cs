using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// Syntax/semantic facts shared by the trigger scan and the condition-effect
/// rule. A condition is a candidate only when it contains
/// <list type="bullet">
/// <item>a <c>++</c>/<c>--</c> whose operand is syntactically a member/index access,</item>
/// <item>an assignment (any kind, incl. <c>??=</c>) to a member/index access, or</item>
/// <item>a <c>ref</c> argument or an <c>out _</c> discard argument.</item>
/// </list>
/// An <c>out</c> argument naming an existing variable (or <c>out var</c>) is
/// NOT a candidate and makes the whole site unsafe: hoisting the condition
/// into a temp loses the compiler's definite-assignment knowledge for that
/// variable (C# only treats it as assigned in the true branch of the original
/// condition expression), which would produce CS0165 at its uses.
/// Lambdas inside the condition are not descended into: their bodies are
/// deferred and are not evaluated as part of the condition.
/// </summary>
internal static class ConditionEffectSyntaxFacts
{
    /// <summary>Cheap syntax-only candidate test (used by the family dirty gate).</summary>
    public static bool HasCandidateSyntax(ExpressionSyntax condition)
        => AnyCandidate(condition, accept: null);

    /// <summary>
    /// Semantic candidate test: at least one syntactic candidate whose target
    /// resolves to a property (indexers included) — <c>ref</c>/<c>out _</c>
    /// arguments are side effects on their own.
    /// </summary>
    public static bool HasSemanticCandidate(
        ExpressionSyntax condition, SemanticModel model)
        => AnyCandidate(condition, node => IsPropertyTarget(model, node));

    /// <summary>
    /// Shapes this slice deliberately preserves: declaration expressions /
    /// pattern variables (<c>out var</c>, <c>is T x</c>) and <c>await</c>.
    /// </summary>
    public static bool ContainsUnsupportedShape(ExpressionSyntax condition)
    {
        foreach (var node in condition.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case DeclarationExpressionSyntax:
                case SingleVariableDesignationSyntax:
                case AwaitExpressionSyntax:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the condition passes an existing variable (not <c>_</c>) to
    /// an <c>out</c> parameter. Such a site is preserved: after hoisting, the
    /// variable's definite-assignment state in the then/body branch is lost.
    /// </summary>
    public static bool ContainsOutVariableArgument(ExpressionSyntax condition)
        => AnyNode(
            condition,
            static node => node is ArgumentSyntax argument
                && argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                && !IsDiscard(argument.Expression));

    /// <summary>True for a literal condition (<c>while (true)</c>, <c>if (false)</c>).</summary>
    public static bool IsLiteralCondition(ExpressionSyntax condition)
        => Unparenthesize(condition) is LiteralExpressionSyntax;

    private static bool AnyCandidate(
        SyntaxNode node, Func<SyntaxNode, bool>? accept)
    {
        if (IsCandidateNode(node) && (accept == null || accept(node)))
            return true;

        foreach (var child in node.ChildNodes())
        {
            if (child is AnonymousFunctionExpressionSyntax)
                continue;
            if (AnyCandidate(child, accept))
                return true;
        }

        return false;
    }

    private static bool AnyNode(
        SyntaxNode node, Func<SyntaxNode, bool> predicate)
    {
        if (predicate(node))
            return true;

        foreach (var child in node.ChildNodes())
        {
            if (child is AnonymousFunctionExpressionSyntax)
                continue;
            if (AnyNode(child, predicate))
                return true;
        }

        return false;
    }

    private static bool IsCandidateNode(SyntaxNode node)
    {
        switch (node)
        {
            case PrefixUnaryExpressionSyntax prefix:
                return IsIncrementOrDecrement(prefix.Kind())
                       && IsMemberOrElementTarget(prefix.Operand);

            case PostfixUnaryExpressionSyntax postfix:
                return IsIncrementOrDecrement(postfix.Kind())
                       && IsMemberOrElementTarget(postfix.Operand);

            case AssignmentExpressionSyntax assignment:
                return IsMemberOrElementTarget(assignment.Left);

            case ArgumentSyntax argument:
                if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                    return true;

                // `out _` has no variable to track; `out x` / `out var x` are
                // handled by ContainsOutVariableArgument (site preserved).
                return argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                       && IsDiscard(argument.Expression);

            default:
                return false;
        }
    }

    private static bool IsPropertyTarget(SemanticModel model, SyntaxNode node)
    {
        if (node is ArgumentSyntax)
            return true;

        var target = node switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            AssignmentExpressionSyntax assignment => assignment.Left,
            _ => null,
        };

        return target != null
               && model.GetSymbolInfo(Unparenthesize(target)).Symbol
                   is IPropertySymbol;
    }

    private static bool IsIncrementOrDecrement(SyntaxKind kind)
        => kind is SyntaxKind.PreIncrementExpression
            or SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostIncrementExpression
            or SyntaxKind.PostDecrementExpression;

    private static bool IsMemberOrElementTarget(ExpressionSyntax expression)
        => Unparenthesize(expression) is MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax;

    private static bool IsDiscard(ExpressionSyntax expression)
        => Unparenthesize(expression) is IdentifierNameSyntax
        {
            Identifier.Text: "_",
        };

    private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }
}
