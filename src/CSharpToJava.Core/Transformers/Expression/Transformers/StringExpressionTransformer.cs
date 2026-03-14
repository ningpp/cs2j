using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

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
        // TODO: Implement interpolated string transformation
        return $"/* TODO: interpolated string */ {node}";
    }
}
