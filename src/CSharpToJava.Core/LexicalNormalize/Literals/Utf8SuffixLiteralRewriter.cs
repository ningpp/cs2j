using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-2: "x"u8 → byte[] array carrying the compile-time UTF-8 blob (design
/// D4/D5). Byte computation uses Encoding.UTF8 (Core-side only); the M0 probe
/// (Task 1) locks the lone-surrogate behavior. C# 11 implicit span conversion
/// covers ReadOnlySpan&lt;byte&gt; use sites; verify build gates them. Member
/// access is not such a site (extension methods are never found through a
/// user-defined implicit conversion), so a receiver keeps the span type.
/// </summary>
internal static class Utf8SuffixLiteralRewriter
{
    public static ExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        if (LiteralSyntaxFacts.Classify(node) != LiteralKind.Utf8)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(node.Token.ValueText);
        ExpressionSyntax replacement = ByteLiteralFactory.Create(bytes).WithTriviaFrom(node);
        recorder.RecordUtf8(bytes);
        return AsMemberAccessReceiver(replacement, node);
    }

    /// <summary>
    /// `"x"u8.TryCopyTo(...)` relies on the literal's ReadOnlySpan&lt;byte&gt;
    /// type for extension-method lookup; the emitted byte[] would reject it
    /// (CS1061). Cast the array back to the span type only in receiver
    /// position, leaving every other use site as the bare array. The cast must
    /// be parenthesized: a bare `(T)new byte[]{…}.M()` binds the member access
    /// to the array first, turning `"x"u8.ToArray()` into a span-typed value.
    /// </summary>
    private static ExpressionSyntax AsMemberAccessReceiver(
        ExpressionSyntax array,
        LiteralExpressionSyntax node)
    {
        if (node.Parent is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Expression != node)
        {
            return array;
        }

        var spanType = SyntaxFactory.ParseTypeName("global::System.ReadOnlySpan<byte>");
        var cast = SyntaxFactory.CastExpression(spanType, array.WithoutTrivia());
        return SyntaxFactory.ParenthesizedExpression(cast).WithTriviaFrom(node);
    }
}
