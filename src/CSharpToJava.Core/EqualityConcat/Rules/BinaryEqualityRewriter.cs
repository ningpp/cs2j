using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EqualityConcat.Rules;

/// <summary>
/// Rewriter for equality sites EC-1 (string ==/!=), EC-2 (object identity,
/// incl. <c>(object)</c> escapes) and EC-7 (record class/struct synthesized
/// ==). EC-6 delegate equality rendering lives in
/// <see cref="DelegateEqualRewriter"/>.
/// </summary>
internal static class BinaryEqualityRewriter
{
    /// <summary>EC-1: <c>string.Equals(a, b)</c> (static two-argument overload).</summary>
    public static ExpressionSyntax RewriteStringEquality(
        ExpressionSyntax left,
        ExpressionSyntax right,
        bool notEquals)
        => NegateIfNeeded(
            InvokeOnPredefined(SyntaxKind.StringKeyword, "Equals", left, right),
            notEquals);

    /// <summary>EC-2: <c>object.ReferenceEquals(a, b)</c>.</summary>
    public static ExpressionSyntax RewriteObjectIdentity(
        ExpressionSyntax left,
        ExpressionSyntax right,
        bool notEquals)
        => NegateIfNeeded(
            InvokeOnPredefined(SyntaxKind.ObjectKeyword, "ReferenceEquals", left, right),
            notEquals);

    /// <summary>EC-7: <c>object.Equals(a, b)</c> (virtual dispatch into the record value Equals).</summary>
    public static ExpressionSyntax RewriteRecordValue(
        ExpressionSyntax left,
        ExpressionSyntax right,
        bool notEquals)
        => NegateIfNeeded(
            InvokeOnPredefined(SyntaxKind.ObjectKeyword, "Equals", left, right),
            notEquals);

    /// <summary>
    /// Builds <c>keyword.Method(left, right)</c> where keyword is a predefined
    /// type keyword (<c>string</c>/<c>object</c>), which always resolves.
    /// </summary>
    private static InvocationExpressionSyntax InvokeOnPredefined(
        SyntaxKind typeKeyword,
        string methodName,
        ExpressionSyntax left,
        ExpressionSyntax right)
    {
        var receiver = SyntaxFactory.PredefinedType(SyntaxFactory.Token(typeKeyword));
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            SyntaxFactory.IdentifierName(methodName));

        var argumentList = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(left),
                SyntaxFactory.Argument(right),
            }));

        return SyntaxFactory.InvocationExpression(memberAccess, argumentList);
    }

    /// <summary>Wraps <c>!expression</c> when the site is <c>!=</c>.</summary>
    public static ExpressionSyntax NegateIfNeeded(
        ExpressionSyntax expression,
        bool negate)
        => negate
            ? SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression, expression)
            : expression;
}
