using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles literal expressions (numeric, string, char, null, true/false).
/// Implements <see cref="IIRExpressionTransformer"/> to produce structured
/// <see cref="JavaLiteralExpression"/> IR nodes.
/// </summary>
[TransformerRegistration]
public class LiteralExpressionTransformer : IIRExpressionTransformer
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
            SyntaxKind.FalseLiteralExpression,
            SyntaxKind.Utf8StringLiteralExpression,
        }, new LiteralExpressionTransformer());
    }

    private static readonly Lazy<LiteralExpressionTransformer> _instance = new(() => new());
    public static LiteralExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.NumericLiteralExpression => TransformNumericLiteral((LiteralExpressionSyntax)node, context),
            SyntaxKind.StringLiteralExpression => TransformStringLiteral((LiteralExpressionSyntax)node, context),
            SyntaxKind.CharacterLiteralExpression => TransformCharacterLiteral((LiteralExpressionSyntax)node),
            SyntaxKind.NullLiteralExpression => "null",
            SyntaxKind.TrueLiteralExpression => "true",
            SyntaxKind.FalseLiteralExpression => "false",
            SyntaxKind.Utf8StringLiteralExpression => TransformUtf8StringLiteral((LiteralExpressionSyntax)node),
            _ => throw new NotSupportedException($"Literal kind {node.Kind()} not supported.")
        };

    /// <summary>
    /// Produces a structured <see cref="JavaLiteralExpression"/> IR node for the literal.
    /// </summary>
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var code = Transform(node, context);
        return new JavaLiteralExpression(code);
    }

    private string TransformNumericLiteral(LiteralExpressionSyntax node, ConversionContext context)
    {
        var token = node.Token;
        var text = token.Text;

        // Fix 5: Guard digit separators on Java version (Java 7+ required)
        string literal = ((int)context.Options.TargetJavaVersion < 7 && text.Contains('_'))
            ? text.Replace("_", "")
            : text;

        // C# type suffixes (f, d, m, u, l) only apply to decimal literals.
        // For hex/binary literals, letters A-F are valid digits, not suffixes.
        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return literal;

        // Handle suffixes
        if (literal.EndsWith("f") || literal.EndsWith("F"))
        {
            return literal.TrimEnd('f', 'F') + "f";
        }
        if (literal.EndsWith("d") || literal.EndsWith("D"))
        {
            var numPart = literal.TrimEnd('d', 'D');
            // Ensure the result is still a double literal in Java.
            // "1d" → "1.0" (not "1" which Java interprets as int).
            return numPart.Contains('.') ? numPart : numPart + ".0";
        }
        // Fix 1: Map decimal (m/M suffix) to BigDecimal
        if (literal.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            string decStr = literal.TrimEnd('m', 'M');
            context.AddImport("java.math.BigDecimal");
            return $"new BigDecimal(\"{decStr}\")";
        }
        // Fix 2: Handle UL/ul/Lu/LU suffix — ulong literals that may exceed long.MaxValue
        if (literal.EndsWith("ul", StringComparison.OrdinalIgnoreCase) ||
            literal.EndsWith("lu", StringComparison.OrdinalIgnoreCase))
        {
            string numPart = literal.TrimEnd('u', 'U', 'l', 'L');
            if (TryParseNumericValue(numPart, out ulong ulongValue) && ulongValue > (ulong)long.MaxValue)
            {
                context.AddImport("java.math.BigInteger");
                return $"new BigInteger(\"{ulongValue}\")";
            }
            return numPart + "L"; // fits in Java long
        }
        // Fix 2: Handle U/u suffix — uint literals that may exceed int.MaxValue
        if (literal.EndsWith("u") || literal.EndsWith("U"))
        {
            string numPart = literal.TrimEnd('u', 'U');
            if (TryParseNumericValue(numPart, out ulong uintValue) && uintValue > (ulong)int.MaxValue)
                return numPart + "L"; // widen to Java long
            return numPart;
        }
        if (literal.EndsWith("l") || literal.EndsWith("L"))
        {
            return literal.TrimEnd('l', 'L') + "L";
        }

        return literal;
    }

    private string TransformStringLiteral(LiteralExpressionSyntax node, ConversionContext context)
    {
        var token = node.Token;

        // Fix 3: Handle raw string literals (C# 11 """...""")
        if (token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
        {
            if ((int)context.Options.TargetJavaVersion >= 15)
            {
                // Emit as Java text block
                string content = token.ValueText;
                if (!content.EndsWith("\n"))
                    content += "\n";
                return $"\"\"\"\n{content}\"\"\"";
            }
            // Fallback: escape and emit as regular string
            var escaped = token.ValueText
                .Replace("\\", "\\\\")   // \ → \\
                .Replace("\"", "\\\"")   // " → \"
                .Replace("\r\n", "\\n")  // CRLF → \n
                .Replace("\n", "\\n")    // LF → \n
                .Replace("\r", "\\n")    // CR → \n
                .Replace("\t", "\\t");   // TAB → \t
            return "\"" + escaped + "\"";
        }

        // Handle verbatim strings (@"...")
        if (token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var text = token.Text;
            if (text.StartsWith("@") || text.StartsWith("$@") || text.StartsWith("@$"))
            {
                // Use the semantic value (already decoded from C# verbatim encoding)
                // and re-encode for Java string literals
                var value = token.ValueText;
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

        return token.Text;
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

    // Fix 4: Emit a byte-array literal for UTF-8 string literals ("..."u8)
    private static string TransformUtf8StringLiteral(LiteralExpressionSyntax node)
    {
        var value = node.Token.ValueText;
        var bytes = Encoding.UTF8.GetBytes(value);
        var byteArr = string.Join(", ", bytes.Select(b => $"(byte)0x{b:X2}"));
        return $"new byte[]{{ {byteArr} }}";
    }

    /// <summary>
    /// Parse a numeric literal string (decimal, hex 0x, binary 0b) to a ulong.
    /// Handles digit separators (underscores).
    /// </summary>
    private static bool TryParseNumericValue(string text, out ulong value)
    {
        try
        {
            string cleaned = text.Replace("_", "");
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToUInt64(cleaned.Substring(2), 16);
            else if (cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToUInt64(cleaned.Substring(2), 2);
            else
                value = ulong.Parse(cleaned);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }
}
