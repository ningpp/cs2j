using Microsoft.CodeAnalysis;
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

    // ------------------------------------------------------------------
    // NA-6: ??= inside a conditional access (design §3.5). The coalesced
    // member is read exactly once into an inner __ed_nN temp; the actual
    // assignment happens only when that temp is null.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>var __ed_nN = receiver.&lt;binding&gt;;</c> — the single member
    /// read used by the null test.
    /// </summary>
    public static LocalDeclarationStatementSyntax BuildInnerTempDeclaration(
        string innerTempName,
        ExpressionSyntax assignmentLeft,
        ExpressionSyntax receiver)
    {
        var memberRead = NullConditionalEmitter.ReplaceRootBinding(
            assignmentLeft, receiver);
        return ExpressionDesugarTempLifter.DeclareVar(innerTempName, memberRead);
    }

    /// <summary><c>__ed_nN = receiver.&lt;binding&gt;;</c> (guarded prefetch).</summary>
    public static ExpressionStatementSyntax BuildInnerTempAssignment(
        string innerTempName,
        ExpressionSyntax assignmentLeft,
        ExpressionSyntax receiver)
    {
        var memberRead = NullConditionalEmitter.ReplaceRootBinding(
            assignmentLeft, receiver);
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(innerTempName),
                memberRead));
    }

    /// <summary><c>T? __ed_nN = null;</c> for the value-form prefetch.</summary>
    public static LocalDeclarationStatementSyntax DeclareInnerNull(
        string innerTempName, ITypeSymbol? memberType)
    {
        TypeSyntax typeSyntax;
        if (memberType == null)
        {
            typeSyntax = SyntaxFactory.NullableType(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)));
        }
        else
        {
            var parsed = SyntaxFactory.ParseTypeName(memberType.ToDisplayString());
            var nullableValueType = memberType is INamedTypeSymbol named
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            var annotatedReference = memberType.IsReferenceType
                && memberType.NullableAnnotation == NullableAnnotation.Annotated;
            typeSyntax = nullableValueType || annotatedReference
                ? parsed
                : SyntaxFactory.NullableType(parsed);
        }

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax)
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(innerTempName)
                            .WithInitializer(
                                SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.NullLiteralExpression))))));
    }

    /// <summary>
    /// Deepest guard body for the statement form:
    /// <c>var nN = s.b; if (nN == null) s.b = c;</c> (two statements). The
    /// coalesce operator degrades to a plain <c>=</c> — the null test has
    /// already established the target is null.
    /// </summary>
    public static List<StatementSyntax> BuildCoalesceDeepestBlock(
        ChainPlan plan,
        AssignmentExpressionSyntax visitedAssignment,
        ExpressionSyntax receiver)
    {
        var innerName = plan.InnerTempName;

        var innerDeclaration = BuildInnerTempDeclaration(
            innerName, visitedAssignment.Left, receiver);

        var replacedAssignment = ReplaceAssignmentLeft(
            visitedAssignment, receiver);
        var simpleAssignment = WithSimpleEquals(replacedAssignment);
        var nullIf = SyntaxFactory.IfStatement(
            IsNullCheck(SyntaxFactory.IdentifierName(innerName)),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(simpleAssignment)));

        return new List<StatementSyntax> { innerDeclaration, nullIf };
    }

    /// <summary>
    /// Final value ternary for the NA-6 value form:
    /// <c>sN != null ? (nN == null ? (sN.b = c) : nN) : null</c>.
    /// Outer parentheses are required for the same enclosing-?? reason as
    /// the read-side nested ternary.
    /// </summary>
    public static ExpressionSyntax BuildCoalesceValueTernary(
        ChainPlan plan,
        AssignmentExpressionSyntax visitedAssignment)
        => SyntaxFactory.ParenthesizedExpression(
            BuildCoalesceValueLevel(plan, visitedAssignment, 0));

    private static ConditionalExpressionSyntax BuildCoalesceValueLevel(
        ChainPlan plan,
        AssignmentExpressionSyntax visitedAssignment,
        int k)
    {
        var receiver = SyntaxFactory.IdentifierName(
            plan.Segments[k].TempName);

        ExpressionSyntax whenTrue;
        if (k == plan.Segments.Count - 1)
        {
            var replacedAssignment = ReplaceAssignmentLeft(
                visitedAssignment, receiver);
            // Known-null branch: plain '=' instead of the original ??=.
            var simpleAssignment = WithSimpleEquals(replacedAssignment);
            whenTrue = SyntaxFactory.ConditionalExpression(
                IsNullCheck(SyntaxFactory.IdentifierName(plan.InnerTempName)),
                simpleAssignment,
                SyntaxFactory.IdentifierName(plan.InnerTempName));
        }
        else
        {
            whenTrue = BuildCoalesceValueLevel(
                plan, visitedAssignment, k + 1);
        }

        return SyntaxFactory.ConditionalExpression(
            NotNullCheck(receiver),
            whenTrue,
            NullConditionalEmitter.BuildNullBranch(plan));
    }

    /// <summary><c>receiver == null</c>.</summary>
    private static BinaryExpressionSyntax IsNullCheck(ExpressionSyntax receiver)
        => SyntaxFactory.BinaryExpression(
            SyntaxKind.EqualsExpression,
            receiver,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

    /// <summary>
    /// Replaces an assignment operator token with a plain <c>=</c> (NA-6
    /// known-null branch: the original <c>??=</c> has been tested).
    /// </summary>
    private static AssignmentExpressionSyntax WithSimpleEquals(
        AssignmentExpressionSyntax assignment)
        => assignment.WithOperatorToken(
            SyntaxFactory.Token(SyntaxKind.EqualsToken));

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
