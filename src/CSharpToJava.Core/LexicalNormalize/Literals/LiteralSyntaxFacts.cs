using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Token-level classification for LL-1/2/3/6 (design §4.2, D13). Purely
/// syntactic: SyntaxKind, the verbatim '@' prefix and backslash pairing.
/// </summary>
internal static class LiteralSyntaxFacts
{
    public static LiteralKind Classify(LiteralExpressionSyntax node)
    {
        var token = node.Token;

        if (token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
        {
            return LiteralKind.RawSingleLine;
        }

        if (token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
        {
            return LiteralKind.RawMultiLine;
        }

        if (token.IsKind(SyntaxKind.Utf8StringLiteralToken))
        {
            return LiteralKind.Utf8;
        }

        if (token.IsKind(SyntaxKind.StringLiteralToken))
        {
            if (token.Text.StartsWith("@\"", StringComparison.Ordinal))
            {
                return LiteralKind.Verbatim;
            }

            return ContainsBareEsc(token.Text) ? LiteralKind.EscString : LiteralKind.Regular;
        }

        if (token.IsKind(SyntaxKind.CharacterLiteralToken)
            && ContainsBareEsc(token.Text))
        {
            return LiteralKind.EscChar;
        }

        return LiteralKind.Other;
    }

    public static bool IsInterpolatedRaw(SyntaxKind startKind)
        => startKind is SyntaxKind.InterpolatedSingleLineRawStringStartToken
            or SyntaxKind.InterpolatedMultiLineRawStringStartToken;

    public static bool IsInterpolatedVerbatim(SyntaxKind startKind)
        => startKind == SyntaxKind.InterpolatedVerbatimStringStartToken;

    /// <summary>
    /// True when <paramref name="text"/> contains a '\e' escape that is not
    /// itself backslash-escaped: the source text "\\e" (two backslashes
    /// followed by letter e) must NOT match. Scans runs of consecutive
    /// backslashes; an odd-length run immediately followed by 'e' is bare.
    /// </summary>
    public static bool ContainsBareEsc(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            var run = 1;
            while (i + run < text.Length && text[i + run] == '\\')
            {
                run++;
            }

            if (i + run < text.Length
                && text[i + run] == 'e'
                && (run & 1) == 1)
            {
                return true;
            }

            i += run - 1; // the loop's i++ then moves just past the run
        }

        return false;
    }
}
