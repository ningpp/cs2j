using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 object-initializer emission (design §4 IN-2): the ordered
/// property/field assignment statements applied to the receiver temp. Property
/// writes stay property writes (<c>__ed_n.P = v</c>), field writes are direct
/// (<c>__ed_n.F = v</c>); member RHS expressions are reused verbatim (zero
/// copies) in strict source order. Nested create / nested member-init / index
/// items are M2 and never reach this emitter.
/// </summary>
internal static class ObjectInitRewriter
{
    /// <summary>
    /// Builds the assignment statement for one object-initializer member plan,
    /// using the (already visited) initializer item for the RHS.
    /// </summary>
    public static StatementSyntax BuildMemberStatement(
        string temp,
        InitializerMemberPlan member,
        ExpressionSyntax visitedElement)
    {
        var memberName = member switch
        {
            PropertySetPlan property => property.Property?.Name,
            FieldSetPlan field => field.Field?.Name,
            _ => null,
        };

        var value = visitedElement is AssignmentExpressionSyntax assignment
            ? assignment.Right
            : visitedElement;

        var target = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(temp),
            SyntaxFactory.IdentifierName(memberName ?? "_"));

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
