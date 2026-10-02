using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 emission helpers (design §4 IN-2/IN-4/IN-5/IN-8): construction
/// expression, member-access receiver and the ordered property/field/index
/// assignment statements applied to a receiver temp. Member RHS expressions are
/// reused verbatim (zero copies); nested creation / nested member-init /
/// collection items are dispatched by <see cref="InitializerRewriter"/>.
/// </summary>
internal static class ObjectInitRewriter
{
    /// <summary>Builds <c>receiver.Name</c>.</summary>
    public static ExpressionSyntax MemberAccess(string receiver, string name)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(receiver),
            SyntaxFactory.IdentifierName(name));

    /// <summary>Builds <c>receiver.Name = value;</c>.</summary>
    public static StatementSyntax BuildMemberAssignment(
        string receiver, string name, ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                MemberAccess(receiver, name),
                value));

    /// <summary>Builds <c>receiver[k1, k2] = value;</c> from the item's left-hand side.</summary>
    public static StatementSyntax BuildIndexAssignment(
        string receiver, ExpressionSyntax visitedLeft, ExpressionSyntax value)
    {
        var arguments = visitedLeft switch
        {
            ImplicitElementAccessSyntax implicitElement => implicitElement.ArgumentList,
            ElementAccessExpressionSyntax element => element.ArgumentList,
            _ => SyntaxFactory.BracketedArgumentList(),
        };

        var target = SyntaxFactory.ElementAccessExpression(
            SyntaxFactory.IdentifierName(receiver), arguments);

        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, target, value));
    }

    /// <summary>
    /// Builds the receiver construction expression with the initializer
    /// stripped: <c>new C(args)</c> for explicit creations and
    /// <c>new T(args)</c> (explicit type from the symbol) for target-typed
    /// <c>new()</c> (design §4 IN-3: constructor arguments preserved verbatim).
    /// </summary>
    public static ObjectCreationExpressionSyntax BuildConstruction(
        InitializerPlan plan, BaseObjectCreationExpressionSyntax visited)
    {
        var typeSyntax = visited is ObjectCreationExpressionSyntax explicitCreation
            ? explicitCreation.Type
            : TargetTypedNewRewriter.BuildTypeSyntax(plan.CreatedType);

        var argumentList = visited switch
        {
            ObjectCreationExpressionSyntax { ArgumentList: { } args } => args,
            ImplicitObjectCreationExpressionSyntax implicitCreation =>
                implicitCreation.ArgumentList,
            _ => SyntaxFactory.ArgumentList(),
        };

        return SyntaxFactory.ObjectCreationExpression(typeSyntax)
            .WithArgumentList(argumentList ?? SyntaxFactory.ArgumentList());
    }
}
