using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles string expressions (interpolated strings).
/// </summary>
[TransformerRegistration]
public class StringExpressionTransformer : IIRExpressionTransformer
{
    static StringExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.InterpolatedStringExpression
        }, new StringExpressionTransformer());
    }

    private static readonly Lazy<StringExpressionTransformer> _instance = new(() => new());
    public static StringExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.InterpolatedStringExpression => TransformInterpolatedString((InterpolatedStringExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"String expression kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not InterpolatedStringExpressionSyntax interpNode)
            return new JavaRawExpression(Transform(node, context));

        var facade = ExpressionTransformerFacade.Instance;

        // Detect string kind
        var startTokenKind = interpNode.StringStartToken.Kind();
        var isVerbatim = startTokenKind == SyntaxKind.InterpolatedVerbatimStringStartToken;
        var isRaw = startTokenKind == SyntaxKind.InterpolatedSingleLineRawStringStartToken
                 || startTokenKind == SyntaxKind.InterpolatedMultiLineRawStringStartToken;

        bool hasFormatSpecifiers = interpNode.Contents
            .OfType<InterpolationSyntax>()
            .Any(i => i.FormatClause != null);

        // String.format path → JavaMethodCallExpression(String, "format", [...])
        if (hasFormatSpecifiers)
        {
            var code = EmitStringFormat(interpNode, isVerbatim, isRaw, context);
            return new JavaRawExpression(code);
        }

        // Concatenation path — collect parts as IR nodes
        var parts = new List<JavaExpression>();
        var currentText = new StringBuilder();

        foreach (var content in interpNode.Contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                currentText.Append(GetProcessedText(textSyntax.TextToken.Text, isVerbatim, isRaw));
            }
            else if (content is InterpolationSyntax interpolation)
            {
                if (currentText.Length > 0)
                {
                    parts.Add(new JavaLiteralExpression { Value = $"\"{currentText}\"" });
                    currentText.Clear();
                }

                var exprIR = facade.TransformToIR(interpolation.Expression, context);
                var exprType = context.SemanticModel?.GetTypeInfo(interpolation.Expression);
                if (exprType.HasValue && exprType.Value.Type != null)
                {
                    var typeName = context.MapType(exprType.Value.Type);
                    if (!IsStringType(typeName) && !IsPrimitiveType(typeName))
                    {
                        // Wrap in String.valueOf(expr)
                        var valueOf = new JavaMethodCallExpression
                        {
                            Target = new JavaIdentifierExpression { Name = "String" },
                            MethodName = "valueOf"
                        };
                        valueOf.Arguments.Add(exprIR);
                        exprIR = valueOf;
                    }
                }
                parts.Add(exprIR);
            }
        }

        if (currentText.Length > 0)
            parts.Add(new JavaLiteralExpression { Value = $"\"{currentText}\"" });

        if (parts.Count == 0) return new JavaLiteralExpression { Value = "\"\"" };
        if (parts.Count == 1) return parts[0];

        // 4+ parts → StringBuilder — fall back to raw (complex chain)
        if (parts.Count >= 4)
        {
            var code = Transform(node, context);
            return new JavaRawExpression(code);
        }

        // 2-3 parts → JavaBinaryExpression chain with "+"
        JavaExpression result = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            result = new JavaBinaryExpression { Left = result, Operator = "+", Right = parts[i] };
        }
        return result;
    }

    private string TransformInterpolatedString(InterpolatedStringExpressionSyntax node, ConversionContext context)
    {
        // Fix 3: Detect string kind via start token (handles $@"..." verbatim and $"""...""" raw)
        var startTokenKind = node.StringStartToken.Kind();
        var isVerbatim = startTokenKind == SyntaxKind.InterpolatedVerbatimStringStartToken;
        var isRaw = startTokenKind == SyntaxKind.InterpolatedSingleLineRawStringStartToken
                 || startTokenKind == SyntaxKind.InterpolatedMultiLineRawStringStartToken;

        // Fix 5: Choose output strategy based on whether any interpolation has a format specifier
        bool hasFormatSpecifiers = node.Contents
            .OfType<InterpolationSyntax>()
            .Any(i => i.FormatClause != null);

        if (hasFormatSpecifiers)
            return EmitStringFormat(node, isVerbatim, isRaw, context);

        return ConcatenateOnly(node, isVerbatim, isRaw, context);
    }

    // Fix 5: Concatenation path — used when no interpolation has a format specifier
    private string ConcatenateOnly(InterpolatedStringExpressionSyntax node, bool isVerbatim, bool isRaw, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var parts = new List<string>();
        var currentText = new StringBuilder();

        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                currentText.Append(GetProcessedText(textSyntax.TextToken.Text, isVerbatim, isRaw));
            }
            else if (content is InterpolationSyntax interpolation)
            {
                if (currentText.Length > 0)
                {
                    parts.Add($"\"{currentText}\"");
                    currentText.Clear();
                }

                var expr = facade.Transform(interpolation.Expression, context);
                var exprType = context.SemanticModel?.GetTypeInfo(interpolation.Expression);
                if (exprType.HasValue && exprType.Value.Type != null)
                {
                    var typeName = context.MapType(exprType.Value.Type);
                    if (!IsStringType(typeName) && !IsPrimitiveType(typeName))
                    {
                        expr = $"String.valueOf({expr})";
                    }
                }
                parts.Add(expr);
            }
        }

        if (currentText.Length > 0)
            parts.Add($"\"{currentText}\"");

        if (parts.Count == 0) return "\"\"";
        if (parts.Count == 1) return parts[0];

        // Fix 2: Use StringBuilder for strings with 4+ concatenation parts
        if (parts.Count >= 4)
        {
            string appends = string.Join("", parts.Select(p => $".append({p})"));
            return $"new StringBuilder(){appends}.toString()";
        }

        return string.Join(" + ", parts);
    }

    // Fix 5: String.format path — used when at least one interpolation has a format specifier
    private string EmitStringFormat(InterpolatedStringExpressionSyntax node, bool isVerbatim, bool isRaw, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var formatSb = new StringBuilder();
        var args = new List<string>();

        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                var text = GetProcessedText(textSyntax.TextToken.Text, isVerbatim, isRaw);
                // Escape literal % characters so they don't become format specifiers
                text = text.Replace("%", "%%");
                formatSb.Append(text);
            }
            else if (content is InterpolationSyntax interpolation)
            {
                string formatSpec = interpolation.FormatClause != null
                    ? ConvertCSharpFormatToJava(interpolation.FormatClause.FormatStringToken.Text)
                    : "%s";
                formatSb.Append(formatSpec);

                var expr = facade.Transform(interpolation.Expression, context);
                args.Add(expr);
            }
        }

        string argsStr = args.Count > 0 ? ", " + string.Join(", ", args) : "";
        return $"String.format(\"{formatSb}\"{argsStr})";
    }

    // Fix 3 & Fix 4: Centralised text processing for all interpolated string kinds
    private static string GetProcessedText(string rawText, bool isVerbatim, bool isRaw)
    {
        string text = rawText;

        if (isVerbatim)
        {
            // Verbatim strings ($@"..."): collapse embedded line breaks into spaces
            text = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        }
        // Fix 3: Raw strings ($"""..."""): let EscapeJavaString handle newlines as \n escapes
        // (newline chars → "\\n", literal backslashes → "\\\\") — no pre-processing needed

        text = EscapeJavaString(text);

        if (isVerbatim)
        {
            // Fix 4: In verbatim strings, "" encodes a literal double-quote.
            // EscapeJavaString turns each " into \", so "" becomes \"\".
            // Collapse \"\" → \" so the Java string contains a single escaped quote.
            text = text.Replace("\\\"\\\"", "\\\"");
        }

        return text;
    }

    private static string EscapeJavaString(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\t", "\\t")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    // Fix 1: Complete C# → Java format specifier conversion table
    internal static string ConvertCSharpFormatToJava(string csharpFormat)
    {
        if (string.IsNullOrEmpty(csharpFormat)) return "%s";

        var upper = csharpFormat.ToUpperInvariant();
        var precision = upper.Length > 1 ? upper[1..] : "";

        return upper[0] switch
        {
            // D / d  — integer, optional zero-padded width:  D4 → %04d
            'D' => string.IsNullOrEmpty(precision) ? "%d" : $"%0{precision}d",
            // F / f  — fixed-point decimal:  F2 → %.2f
            'F' => string.IsNullOrEmpty(precision) ? "%f" : $"%.{precision}f",
            // E / e  — scientific notation:  E3 → %.3e
            'E' => string.IsNullOrEmpty(precision) ? "%e" : $"%.{precision}e",
            // X / x  — hexadecimal, preserve case:  X → %X, x4 → %4x
            'X' => string.IsNullOrEmpty(precision)
                    ? (csharpFormat[0] == 'X' ? "%X" : "%x")
                    : $"%{precision}{(csharpFormat[0] == 'X' ? 'X' : 'x')}",
            // N / n  — thousands-separated decimal:  N0 → %,.0f, N2 → %,.2f
            'N' => string.IsNullOrEmpty(precision) ? "%,.0f" : $"%,.{precision}f",
            // G / g  — general (shortest of E or F):  G4 → %.4g
            'G' => string.IsNullOrEmpty(precision) ? "%g" : $"%.{precision}g",
            // P / p  — percentage:  P → %.0f%%, P2 → %.2f%%
            'P' => string.IsNullOrEmpty(precision) ? "%.0f%%" : $"%.{precision}f%%",
            // Unknown specifier — emit %s with a comment so the caller can spot it
            _ => $"%s /* TODO: format specifier {csharpFormat} */"
        };
    }

    private static bool IsStringType(string typeName)
    {
        return typeName == "String" || typeName == "string";
    }

    private static bool IsPrimitiveType(string typeName)
    {
        return typeName is "int" or "long" or "short" or "byte" or "float" or "double" or "boolean" or "char";
    }
}
