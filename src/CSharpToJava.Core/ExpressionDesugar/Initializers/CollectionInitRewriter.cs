using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 collection-initializer emission (design §4 IN-3/IN-6/IN-7): constructor
/// arguments preserved verbatim by <see cref="ObjectInitRewriter.BuildConstruction"/>,
/// then one ordered <c>__ed_n.Add(element)</c> statement per bound Add item.
/// The Add target is the compiler-bound symbol (never a text guess of
/// <c>add</c>/<c>put</c>); the element expression is reused verbatim. M1 emits
/// instance single-argument Adds only (a non-void fluent return is simply
/// discarded by the expression statement).
/// </summary>
internal static class CollectionInitRewriter
{
    /// <summary>Builds <c>__ed_n.Add(element);</c> for one Add item.</summary>
    public static StatementSyntax BuildAddStatement(
        string temp, ExpressionSyntax visitedElement)
    {
        var receiver = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(temp),
            SyntaxFactory.IdentifierName("Add"));

        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                receiver,
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visitedElement)))));
    }
}
