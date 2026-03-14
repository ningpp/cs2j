using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles element access expressions (array/index access, index expressions).
/// </summary>
public class ElementAccessTransformer : IExpressionTransformer
{
    static ElementAccessTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.IndexExpression
        }, new ElementAccessTransformer());
    }

    private static readonly Lazy<ElementAccessTransformer> _instance = new(() => new());
    public static ElementAccessTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ElementAccessExpression => TransformElementAccess((ElementAccessExpressionSyntax)node, context),
            SyntaxKind.IndexExpression => TransformIndexExpression((ElementAccessExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Element access kind {node.Kind()} not supported.")
        };

    private string TransformElementAccess(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement element access transformation
        return $"/* TODO: element access */ {node}";
    }

    private string TransformIndexExpression(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement index expression transformation
        return $"/* TODO: index expression */ {node}";
    }
}
