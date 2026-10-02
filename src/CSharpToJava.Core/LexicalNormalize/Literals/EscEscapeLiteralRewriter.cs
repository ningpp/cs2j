using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-6 (open question 5 adjudicated into C43): bare \e inside a *regular*
/// (non-raw, non-verbatim, non-interpolated) string/char literal → \u001B.
/// Both forms rebuild through SyntaxFactory.Literal from ValueText/Value, so
/// Roslyn owns every other escape (design D13).
/// </summary>
internal static class EscEscapeLiteralRewriter
{
    public static ExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        var kind = LiteralSyntaxFacts.Classify(node);

        if (kind == LiteralKind.EscString)
        {
            var value = node.Token.ValueText;
            var replacement = SyntaxFactory
                .LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(value))
                .WithTriviaFrom(node);

            recorder.RecordEscString(value);
            return replacement;
        }

        if (kind == LiteralKind.EscChar)
        {
            var value = (char)node.Token.Value!;
            var replacement = SyntaxFactory
                .LiteralExpression(
                    SyntaxKind.CharacterLiteralExpression,
                    SyntaxFactory.Literal(value))
                .WithTriviaFrom(node);

            recorder.RecordEscChar(value);
            return replacement;
        }

        return null;
    }
}
