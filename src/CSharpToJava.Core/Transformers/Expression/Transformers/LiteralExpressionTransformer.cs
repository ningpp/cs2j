using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Utilities;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

[TransformerRegistration]
public class LiteralExpressionTransformer : IIRExpressionTransformer
{
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

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var code = Transform(node, context);
        return new JavaLiteralExpression(code);
    }

    private string TransformNumericLiteral(LiteralExpressionSyntax node, ConversionContext context)
    {
        var token = node.Token;
        var text = token.Text;

        string literal = ((int)context.Options.TargetJavaVersion < 7 && text.Contains('_'))
            ? text.Replace("_", "")
            : text;

        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return literal;

        if (literal.EndsWith("f") || literal.EndsWith("F"))
        {
            return literal.TrimEnd('f', 'F') + "f";
        }
        if (literal.EndsWith("d") || literal.EndsWith("D"))
        {
            var numPart = literal.TrimEnd('d', 'D');
            return numPart.Contains('.') ? numPart : numPart + ".0";
        }
        if (literal.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            string decStr = literal.TrimEnd('m', 'M');
            context.AddImport("io.github.ningpp.compat.Decimal");
            return $"Decimal.parse(\"{decStr}\")";
        }
        if (literal.EndsWith("ul", StringComparison.OrdinalIgnoreCase) ||
            literal.EndsWith("lu", StringComparison.OrdinalIgnoreCase))
        {
            string numPart = literal.TrimEnd('u', 'U', 'l', 'L');
            if (TryParseNumericValue(numPart, out ulong ulongValue) && ulongValue > (ulong)long.MaxValue)
            {
                context.AddImport("java.math.BigInteger");
                return $"new BigInteger(\"{ulongValue}\")";
            }
            return numPart + "L";
        }
        if (literal.EndsWith("u") || literal.EndsWith("U"))
        {
            string numPart = literal.TrimEnd('u', 'U');
            if (TryParseNumericValue(numPart, out ulong uintValue) && uintValue > (ulong)int.MaxValue)
                return numPart + "L";
            return numPart;
        }
        if (literal.EndsWith("l") || literal.EndsWith("L"))
        {
            return literal.TrimEnd('l', 'L') + "L";
        }

        if (token.Value is ulong unsuffixedUlong && unsuffixedUlong > long.MaxValue)
        {
            return $"Long.parseUnsignedLong(\"{literal.Replace("_", "")}\")";
        }

        return literal;
    }

    private string TransformStringLiteral(LiteralExpressionSyntax node, ConversionContext context)
    {
        var token = node.Token;

        if (token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken))
        {
            if ((int)context.Options.TargetJavaVersion >= 15)
            {
                string content = token.ValueText;
                if (!content.EndsWith("\n"))
                    content += "\n";
                return $"\"\"\"\n{content}\"\"\"";
            }
            var escaped = StringEscapeHelper.EscapeJavaString(token.ValueText);
            return "\"" + escaped + "\"";
        }

        if (token.IsKind(SyntaxKind.StringLiteralToken))
        {
            var text = token.Text;
            if (text.StartsWith("@") || text.StartsWith("$@") || text.StartsWith("@$"))
            {
                var value = token.ValueText;
                var javaContent = StringEscapeHelper.EscapeJavaString(value);
                return "\"" + javaContent + "\"";
            }

            var escaped = StringEscapeHelper.EscapeJavaString(token.ValueText);
            return "\"" + escaped + "\"";
        }

        return token.Text;
    }

    private string TransformCharacterLiteral(LiteralExpressionSyntax node)
    {
        var value = node.Token.ValueText;
        if (value.Length == 1)
        {
            char c = value[0];
            return c switch
            {
                '\n' => "'\\n'",
                '\r' => "'\\r'",
                '\t' => "'\\t'",
                '\0' => "'\\0'",
                '\b' => "'\\b'",
                '\f' => "'\\f'",
                '\\' => "'\\\\'",
                '\'' => "'\\''",
                _ when c < 0x20 => $"'\\u{((int)c):X4}'",
                _ => $"'{c}'"
            };
        }
        return node.Token.Text;
    }

    private static string TransformUtf8StringLiteral(LiteralExpressionSyntax node)
    {
        var value = node.Token.ValueText;
        var bytes = Encoding.UTF8.GetBytes(value);
        var byteArr = string.Join(", ", bytes.Select(b => $"(byte)0x{b:X2}"));
        return $"new byte[]{{ {byteArr} }}";
    }

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
