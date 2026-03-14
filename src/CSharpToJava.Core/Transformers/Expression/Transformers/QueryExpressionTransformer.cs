using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles LINQ query expressions.
/// </summary>
public class QueryExpressionTransformer : IExpressionTransformer
{
    static QueryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.QueryExpression
        }, new QueryExpressionTransformer());
    }

    private static readonly Lazy<QueryExpressionTransformer> _instance = new(() => new());
    public static QueryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.QueryExpression => TransformQuery((QueryExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Query expression kind {node.Kind()} not supported.")
        };

    private string TransformQuery(QueryExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement query transformation
        return $"/* TODO: query */ {node}";
    }
}
