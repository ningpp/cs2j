using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional.Assignment;

/// <summary>
/// Pure rendering for the C51 assignment terminal (design §4.2/§4.3): the
/// assignment LHS substitution, the single-segment value ternary (NA-5) and
/// the multi-segment nested value ternary (M8 value). Every helper is
/// functional and only combines nodes already visited; the RHS and index
/// argument nodes are never copied out of the assignment.
/// </summary>
internal static class NullConditionalAssignmentEmitter
{
    /// <summary>
    /// Rebuilds the assignment with its LHS leftmost binding replaced by an
    /// access on <paramref name="receiver"/> (reuses C35's
    /// <see cref="NullConditionalEmitter.ReplaceRootBinding"/>). The operator
    /// token and the RHS node stay exactly as parsed — compound assignments
    /// are never split (NA-3).
    /// </summary>
    public static AssignmentExpressionSyntax ReplaceAssignmentLeft(
        AssignmentExpressionSyntax assignment,
        ExpressionSyntax receiver)
    {
        var newLeft = NullConditionalEmitter.ReplaceRootBinding(
            assignment.Left, receiver);
        return assignment.WithLeft(newLeft);
    }

    /// <summary>
    /// Single-segment value ternary (NA-5):
    /// <c>receiver != null ? assignment-on-receiver : null</c>, with the
    /// <c>(T?)</c> preserving cast when the result lifts a value type.
    /// </summary>
    public static ConditionalExpressionSyntax BuildSingleValueTernary(
        AssignmentExpressionSyntax replacedAssignment,
        ExpressionSyntax conditionReceiver,
        ChainPlan plan)
    {
        ExpressionSyntax whenTrue = WithLiftedCast(
            plan, replacedAssignment);

        return SyntaxFactory.ConditionalExpression(
            NotNullCheck(conditionReceiver),
            whenTrue,
            NullConditionalEmitter.BuildNullBranch(plan));
    }

    /// <summary>
    /// Multi-segment nested value ternary (M8 value):
    /// <c>s0 != null ? (s1 != null ? ... assignment : null) : null</c>.
    /// Outer parentheses are mandatory for the same reason as the read-side
    /// nested ternary (a surrounding ?? would otherwise bind the last null
    /// branch).
    /// </summary>
    public static ExpressionSyntax BuildNestedValueTernary(
        ChainPlan plan,
        AssignmentExpressionSyntax visitedAssignment)
        => SyntaxFactory.ParenthesizedExpression(
            BuildNestedLevel(plan, visitedAssignment, 0));

    private static ExpressionSyntax BuildNestedLevel(
        ChainPlan plan,
        AssignmentExpressionSyntax visitedAssignment,
        int k)
    {
        var receiver = SyntaxFactory.IdentifierName(
            plan.Segments[k].TempName);

        ExpressionSyntax whenTrue;
        if (k == plan.Segments.Count - 1)
        {
            var replaced = ReplaceAssignmentLeft(
                visitedAssignment, receiver);
            whenTrue = WithLiftedCast(plan, replaced);
        }
        else
        {
            whenTrue = BuildNestedLevel(
                plan, visitedAssignment, k + 1);
        }

        return SyntaxFactory.ConditionalExpression(
            NotNullCheck(receiver),
            whenTrue,
            NullConditionalEmitter.BuildNullBranch(plan));
    }

    /// <summary><c>receiver != null</c>.</summary>
    private static BinaryExpressionSyntax NotNullCheck(ExpressionSyntax receiver)
        => SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            receiver,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

    /// <summary>
    /// Wraps the true-branch expression in the lifted <c>(T?)</c> preserving
    /// cast. An assignment is not a syntactically valid cast operand (the cast
    /// would bind as <c>(T?)lhs = rhs</c>, CS0131), so it is parenthesized
    /// first: <c>(T?)(lhs = rhs)</c>.
    /// </summary>
    private static ExpressionSyntax WithLiftedCast(
        ChainPlan plan, ExpressionSyntax expression)
    {
        if (!plan.CastToNullable)
            return expression;

        if (expression is AssignmentExpressionSyntax)
            expression = SyntaxFactory.ParenthesizedExpression(expression);

        return SyntaxFactory.CastExpression(
            plan.NullableCastType!, expression);
    }
}
