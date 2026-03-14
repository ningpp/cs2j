using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles unary expressions (prefix/postfix operators, address of, pointer indirection).
/// </summary>
public class UnaryExpressionTransformer : IExpressionTransformer
{
    static UnaryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.UnaryPlusExpression,
            SyntaxKind.UnaryMinusExpression,
            SyntaxKind.LogicalNotExpression,
            SyntaxKind.BitwiseNotExpression,
            SyntaxKind.AddressOfExpression,
            SyntaxKind.PointerIndirectionExpression,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PostDecrementExpression,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PreDecrementExpression
        }, new UnaryExpressionTransformer());
    }

    private static readonly Lazy<UnaryExpressionTransformer> _instance = new(() => new());
    public static UnaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "+", context),
            SyntaxKind.UnaryMinusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "-", context),
            SyntaxKind.LogicalNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "!", context),
            SyntaxKind.BitwiseNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "~", context),
            SyntaxKind.AddressOfExpression => TransformAddressOf((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PointerIndirectionExpression => TransformPointerIndirection((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PostIncrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PostDecrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "--", context),
            SyntaxKind.PreIncrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PreDecrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "--", context),
            _ => throw new NotSupportedException($"Unary expression kind {node.Kind()} not supported.")
        };

    private string TransformUnaryExpression(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // TODO: Implement unary expression transformation
        return $"/* TODO: unary expression */ {node}";
    }

    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement address of transformation
        return $"/* TODO: address of */ {node}";
    }

    private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement pointer indirection transformation
        return $"/* TODO: pointer indirection */ {node}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // TODO: Implement postfix transformation
        return $"/* TODO: postfix */ {node}";
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // TODO: Implement prefix transformation
        return $"/* TODO: prefix */ {node}";
    }
}
