using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Text;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles string expressions (interpolated strings).
/// </summary>
public class StringExpressionTransformer : IExpressionTransformer
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

    private string TransformInterpolatedString(InterpolatedStringExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var result = new StringBuilder();

        // Determine the format string start
        var startQuote = node.StringStartToken.Text; // $" or $@" or $"""
        var isVerbatim = startQuote.Contains("@");
        var isMultiLine = startQuote.Count(c => c == '"') > 2;

        var parts = new List<string>();
        var currentText = new StringBuilder();

        foreach (var content in node.Contents)
        {
            if (content is InterpolatedStringTextSyntax textSyntax)
            {
                // Escape special characters for Java
                var text = textSyntax.TextToken.Text;
                if (isVerbatim)
                {
                    // Remove line breaks in verbatim strings for Java
                    text = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                }
                text = EscapeJavaString(text);
                currentText.Append(text);
            }
            else if (content is InterpolationSyntax interpolation)
            {
                // Add any accumulated text first
                if (currentText.Length > 0)
                {
                    parts.Add($"\"{currentText}\"");
                    currentText.Clear();
                }

                // Transform the expression
                var expr = facade.Transform(interpolation.Expression, context);

                // Handle format strings like $"{value:D4}"
                if (interpolation.AlignmentClause != null || interpolation.FormatClause != null)
                {
                    // For formatted interpolations, use String.format()
                    var formatString = "";
                    if (interpolation.FormatClause != null)
                    {
                        formatString = interpolation.FormatClause.FormatStringToken.Text;
                        formatString = ConvertCSharpFormatToJava(formatString);
                    }
                    else
                    {
                        formatString = "%s";
                    }

                    parts.Add($"String.format(\"{formatString}\", {expr})");
                }
                else
                {
                    // Simple concatenation
                    // For non-string types, we need to convert to string
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
        }

        // Add any remaining text
        if (currentText.Length > 0)
        {
            parts.Add($"\"{currentText}\"");
        }

        // Build the result
        if (parts.Count == 0)
        {
            return "\"\"";
        }
        else if (parts.Count == 1)
        {
            return parts[0];
        }
        else
        {
            return string.Join(" + ", parts);
        }
    }

    private static string EscapeJavaString(string text)
    {
        // Escape backslashes and quotes for Java string literals
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\t", "\\t")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private static string ConvertCSharpFormatToJava(string csharpFormat)
    {
        // Convert C# format specifiers to Java format specifiers
        // C# uses format strings like "D4", "F2", "X", etc.
        // Java uses format strings like "%04d", "%.2f", "%x", etc.
        return csharpFormat switch
        {
            // Numeric formats
            "D" or "d" => "%d",
            "F" or "f" => "%f",
            "N" or "n" => "%,f",
            "P" or "p" => "%%",
            "X" or "x" => "%x",
            // If contains precision (e.g., "F2"), convert accordingly
            var f when f.StartsWith("F", StringComparison.OrdinalIgnoreCase) =>
                "%." + (f.Length > 1 ? f.Substring(1) : "0") + "f",
            var d when d.StartsWith("D", StringComparison.OrdinalIgnoreCase) =>
                "%0" + (d.Length > 1 ? d.Substring(1) : "0") + "d",
            _ => "%s" // Default fallback
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
