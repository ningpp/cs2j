using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EqualityConcat.Rules;

/// <summary>
/// Rewriter for delegate equality sites EC-6. Delegate <c>==</c> sites are
/// rewritten to the support-library call
/// <c>Cs2jPrelude.DelegateOps.Equals(a, b)</c>, which preserves C#
/// <see cref="System.Delegate.op_Equality"/> semantics (null == null is true)
/// instead of Java's <c>Object.equals</c> asymmetry. The CLI adapter is
/// responsible for releasing the Cs2jPrelude support library and wiring the
/// project reference.
/// </summary>
internal static class DelegateEqualRewriter
{
    private const string SupportNamespace = "Cs2jPrelude";
    private const string SupportType = "DelegateOps";
    private const string SupportMethod = "Equals";

    /// <summary>
    /// Builds <c>Cs2jPrelude.DelegateOps.Equals(a, b)</c>; the generic type
    /// argument is intentionally omitted so the C# compiler infers it from
    /// the operands.
    /// </summary>
    public static ExpressionSyntax Rewrite(
        ExpressionSyntax left,
        ExpressionSyntax right,
        bool notEquals)
    {
        var receiver = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(SupportNamespace),
                SyntaxFactory.IdentifierName(SupportType)),
            SyntaxFactory.IdentifierName(SupportMethod));

        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(left),
                SyntaxFactory.Argument(right),
            }));
        var invocation = SyntaxFactory.InvocationExpression(receiver, argumentList);

        return BinaryEqualityRewriter.NegateIfNeeded(invocation, notEquals);
    }
}
