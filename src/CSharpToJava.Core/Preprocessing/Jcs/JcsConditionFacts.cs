using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// Shared condition-side-effect fact source (R12 JCS.EVAL scan and R13
/// condition-effect construct scan read the same classification, so the two
/// reports can never disagree about which condition carries a side effect).
/// </summary>
internal static class JcsConditionFacts
{
    /// <summary>
    /// Returns the first side-effecting node inside <paramref name="condition"/>
    /// (post/pre increment or decrement, assignment, out/ref argument, ref
    /// declaration) with a reason string; <c>false</c> when the condition is
    /// pure.
    /// </summary>
    public static bool TryFindViolation(
        ExpressionSyntax condition,
        out SyntaxNode? site,
        out string? reason)
    {
        foreach (var node in condition.DescendantNodesAndSelf())
        {
            var violation = node switch
            {
                PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                        || postfix.IsKind(SyntaxKind.PostDecrementExpression)
                    => "increment/decrement in condition",
                PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                        || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                    => "increment/decrement in condition",
                AssignmentExpressionSyntax => "assignment in condition",
                ArgumentSyntax argument
                    when argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                        || argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    => "out/ref argument in condition",
                DeclarationExpressionSyntax declaration when declaration.Type is RefTypeSyntax
                    => "ref declaration in condition",
                _ => null,
            };

            if (violation is not null)
            {
                site = node;
                reason = violation;
                return true;
            }
        }

        site = null;
        reason = null;
        return false;
    }
}
