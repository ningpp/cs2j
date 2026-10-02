using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-3: non-interpolated verbatim string (@"…") → regular string literal.
/// ValueText keeps source newlines verbatim (no LF normalization — design D6,
/// deliberately different from raw D2⑤); SyntaxFactory.Literal emits them as
/// \r\n escapes. @identifiers are a different lexical family and never reach
/// this classifier.
/// </summary>
internal static class VerbatimLiteralRewriter
{
    public static LiteralExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        if (LiteralSyntaxFacts.Classify(node) != LiteralKind.Verbatim)
        {
            return null;
        }

        var value = node.Token.ValueText;
        var replacement = SyntaxFactory
            .LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value))
            .WithTriviaFrom(node);

        recorder.RecordVerbatim(value);
        return replacement;
    }
}
