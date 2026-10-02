using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-1: non-interpolated raw string literal (single/multi-line) → regular
/// string literal. ValueText already carries Roslyn's delimiter stripping,
/// indentation trimming and LF normalization (design D1/D2); zero hand-written
/// decoding lives here.
/// </summary>
internal static class RawStringLiteralRewriter
{
    public static LiteralExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        var kind = LiteralSyntaxFacts.Classify(node);
        if (kind is not (LiteralKind.RawSingleLine or LiteralKind.RawMultiLine))
        {
            return null;
        }

        var value = node.Token.ValueText;
        var token = SyntaxFactory.Literal(value);
        var replacement = SyntaxFactory
            .LiteralExpression(SyntaxKind.StringLiteralExpression, token)
            .WithTriviaFrom(node);

        recorder.RecordRaw(kind == LiteralKind.RawMultiLine, value);
        return replacement;
    }
}
