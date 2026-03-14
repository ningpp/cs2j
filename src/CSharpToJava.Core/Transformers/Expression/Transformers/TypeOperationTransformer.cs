using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles type-related expressions (cast, is, as, typeof, default, checked, unchecked).
/// </summary>
public class TypeOperationTransformer : IExpressionTransformer
{
    static TypeOperationTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.CastExpression,
            SyntaxKind.IsExpression,
            SyntaxKind.IsPatternExpression,
            SyntaxKind.AsExpression,
            SyntaxKind.TypeOfExpression,
            SyntaxKind.DefaultExpression,
            SyntaxKind.CheckedExpression,
            SyntaxKind.UncheckedExpression
        }, new TypeOperationTransformer());
    }

    private static readonly Lazy<TypeOperationTransformer> _instance = new(() => new());
    public static TypeOperationTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.CastExpression => TransformCast((CastExpressionSyntax)node, context),
            SyntaxKind.IsExpression => TransformIs((BinaryExpressionSyntax)node, context),
            SyntaxKind.IsPatternExpression => TransformIsPattern((IsPatternExpressionSyntax)node, context),
            SyntaxKind.AsExpression => TransformAs((BinaryExpressionSyntax)node, context),
            SyntaxKind.TypeOfExpression => TransformTypeOf((TypeOfExpressionSyntax)node, context),
            SyntaxKind.DefaultExpression => TransformDefault((DefaultExpressionSyntax)node, context),
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Type operation kind {node.Kind()} not supported.")
        };

    private string TransformCast(CastExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement cast transformation
        return $"/* TODO: cast */ {node}";
    }

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement is transformation
        return $"/* TODO: is */ {node}";
    }

    private string TransformIsPattern(IsPatternExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement is pattern transformation
        return $"/* TODO: is pattern */ {node}";
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement as transformation
        return $"/* TODO: as */ {node}";
    }

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement typeof transformation
        return $"/* TODO: typeof */ {node}";
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement default transformation
        return $"/* TODO: default */ {node}";
    }

    private string TransformChecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement checked transformation
        return $"/* TODO: checked */ {node}";
    }

    private string TransformUnchecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement unchecked transformation
        return $"/* TODO: unchecked */ {node}";
    }
}
