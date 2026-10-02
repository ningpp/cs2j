using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-2 emission: byte blob → new byte[]{ (byte)0xXX, … } (empty blob →
/// new byte[]{}). Hex two-digit uppercase, explicit cast, no Encoding symbol
/// in the emitted tree (design D4). UTF-8 computation happens at the caller.
/// </summary>
internal static class ByteLiteralFactory
{
    public static ArrayCreationExpressionSyntax Create(IReadOnlyList<byte> bytes)
    {
        var rankSpecifier = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                SyntaxFactory.OmittedArraySizeExpression()));
        var arrayType = SyntaxFactory.ArrayType(PredefinedByte())
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifier));

        var elements = new List<ExpressionSyntax>(bytes.Count);
        foreach (var value in bytes)
        {
            var literalToken = SyntaxFactory.Literal("0x" + value.ToString("X2"), (int)value);
            var numeric = SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression, literalToken);
            elements.Add(SyntaxFactory.CastExpression(PredefinedByte(), numeric));
        }

        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.ArrayInitializerExpression,
            SyntaxFactory.SeparatedList(elements));

        // The convenience factory's auto-supplied `new` keyword carries no
        // trailing whitespace, which serializes as `newbyte[]` (a parse error).
        // This pass never invokes the Formatter, so the separator space must be
        // attached explicitly (Roslyn 5.0.0, verified by probe 2026-10-02).
        var newKeyword = SyntaxFactory.Token(SyntaxKind.NewKeyword)
            .WithTrailingTrivia(SyntaxFactory.ElasticWhitespace(" "));
        return SyntaxFactory.ArrayCreationExpression(newKeyword, arrayType, initializer);
    }

    private static PredefinedTypeSyntax PredefinedByte()
        => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword));
}
