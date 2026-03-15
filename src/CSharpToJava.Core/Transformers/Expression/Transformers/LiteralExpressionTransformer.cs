using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles literal expressions (numeric, string, char, null, true/false).
/// </summary>
public class LiteralExpressionTransformer : IExpressionTransformer
{
    // Self-register on type initialization
    static LiteralExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.NumericLiteralExpression,
            SyntaxKind.StringLiteralExpression,
            SyntaxKind.CharacterLiteralExpression,
            SyntaxKind.NullLiteralExpression,
            SyntaxKind.TrueLiteralExpression,
            SyntaxKind.FalseLiteralExpression
        }, new LiteralExpressionTransformer());
    }

    private static readonly Lazy<LiteralExpressionTransformer> _instance = new(() => new());
    public static LiteralExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.NumericLiteralExpression => TransformNumericLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.StringLiteralExpression => TransformStringLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.CharacterLiteralExpression => TransformCharacterLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.NullLiteralExpression => "null",
            SyntaxKind.TrueLiteralExpression => "true",
            SyntaxKind.FalseLiteralExpression => "false",
            _ => throw new NotSupportedException($"Literal kind {node.Kind()} not supported.")
        };

    private string TransformNumericLiteral(LiteralExpressionSyntax node)
    {
        var token = node.Token;
        var text = token.Text;

        // Handle suffixes
        if (text.EndsWith("f") || text.EndsWith("F"))
        {
            return text.TrimEnd('f', 'F') + "f";
        }
        if (text.EndsWith("d") || text.EndsWith("D"))
        {
            return text.TrimEnd('d', 'D');
        }
        if (text.EndsWith("m") || text.EndsWith("M"))
        {
            return "/* TODO: decimal */ " + text.TrimEnd('m', 'M');
        }
        if (text.EndsWith("ul") || text.EndsWith("UL"))
        {
            return text.TrimEnd('u', 'U', 'l', 'L') + "L"; // Java long
        }
        if (text.EndsWith("u") || text.EndsWith("U"))
        {
            return text.TrimEnd('u', 'U'); // Java has no unsigned
        }
        if (text.EndsWith("l") || text.EndsWith("L"))
        {
            return text.TrimEnd('l', 'L') + "L";
        }

        return text;
    }

    private string TransformStringLiteral(LiteralExpressionSyntax node)
    {
        // Handle verbatim strings (@"...")
        if (node.Token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var text = node.Token.Text;
            if (text.StartsWith("@") || text.StartsWith("$@") || text.StartsWith("@$"))
            {
                // Use the semantic value (already decoded from C# verbatim encoding)
                // and re-encode for Java string literals
                var value = node.Token.ValueText;
                var javaContent = value
                    .Replace("\\", "\\\\")   // \ → \\
                    .Replace("\"", "\\\"")   // " → \"
                    .Replace("\r\n", "\\n")  // CRLF → \n
                    .Replace("\n", "\\n")    // LF → \n
                    .Replace("\r", "\\n");   // CR → \n
                return "\"" + javaContent + "\"";
            }

            // Regular string
            return text;
        }

        return node.Token.Text;
    }

    private string TransformCharacterLiteral(LiteralExpressionSyntax node)
    {
        return node.Token.Text switch
        {
            "'\\n'" => "'\\n'",
            "'\\r'" => "'\\r'",
            "'\\t'" => "'\\t'",
            "'\\0'" => "'\\0'",
            _ => node.Token.Text
        };
    }
}
