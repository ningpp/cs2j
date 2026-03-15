using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Statement;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles lambda and anonymous method expressions.
/// </summary>
public class LambdaTransformer : IExpressionTransformer
{
    static LambdaTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.AnonymousMethodExpression
        }, new LambdaTransformer());
    }

    private static readonly Lazy<LambdaTransformer> _instance = new(() => new());
    public static LambdaTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ParenthesizedLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.SimpleLambdaExpression => TransformLambda((LambdaExpressionSyntax)node, context),
            SyntaxKind.AnonymousMethodExpression => TransformAnonymousMethod((AnonymousMethodExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Lambda expression kind {node.Kind()} not supported.")
        };

    private string TransformLambda(LambdaExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Collect parameters
        IEnumerable<ParameterSyntax> parameters = node switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter],
            ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters,
            _ => []
        };

        var javaParams = parameters
            .Select(p => ConversionContext.EscapeJavaKeyword(p.Identifier.Text))
            .ToList();

        var paramStr = javaParams.Count == 1 ? javaParams[0] : $"({string.Join(", ", javaParams)})";

        // Generate body
        if (node.Block != null)
        {
            var stmtTransformer = new StatementTransformer();
            var body = stmtTransformer.TransformBlock(node.Block, context);
            return $"{paramStr} -> {{\n{body}\n}}";
        }
        else if (node.ExpressionBody != null)
        {
            var body = facade.Transform(node.ExpressionBody, context);
            return $"{paramStr} -> {body}";
        }

        return $"{paramStr} -> null";
    }

    private string TransformAnonymousMethod(AnonymousMethodExpressionSyntax node, ConversionContext context)
    {
        var stmtTransformer = new StatementTransformer();

        string paramStr;
        if (node.ParameterList != null && node.ParameterList.Parameters.Count > 0)
        {
            var javaParams = node.ParameterList.Parameters
                .Select(p => ConversionContext.EscapeJavaKeyword(p.Identifier.Text))
                .ToList();
            paramStr = $"({string.Join(", ", javaParams)})";
        }
        else
        {
            paramStr = "()";
        }

        var body = stmtTransformer.TransformBlock(node.Block, context);
        return $"{paramStr} -> {{\n{body}\n}}";
    }
}

