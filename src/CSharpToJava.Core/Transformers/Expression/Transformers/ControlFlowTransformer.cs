using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles control flow expressions (conditional, conditional access, await, throw, switch, this, base, etc.).
/// </summary>
public class ControlFlowTransformer : IExpressionTransformer
{
    static ControlFlowTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ConditionalExpression,
            SyntaxKind.ConditionalAccessExpression,
            SyntaxKind.AwaitExpression,
            SyntaxKind.ThrowExpression,
            SyntaxKind.SwitchExpression,
            SyntaxKind.ThisExpression,
            SyntaxKind.BaseExpression,
            SyntaxKind.ParenthesizedExpression,
            SyntaxKind.ArgListExpression,
            SyntaxKind.MakeRefExpression,
            SyntaxKind.RefTypeExpression,
            SyntaxKind.RefValueExpression,
            SyntaxKind.WithExpression,
            SyntaxKind.RangeExpression
        }, new ControlFlowTransformer());
    }

    private static readonly Lazy<ControlFlowTransformer> _instance = new(() => new());
    public static ControlFlowTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ConditionalExpression => TransformConditional((ConditionalExpressionSyntax)node, context),
            SyntaxKind.ConditionalAccessExpression => TransformConditionalAccess((ConditionalAccessExpressionSyntax)node, context),
            SyntaxKind.AwaitExpression => TransformAwait((AwaitExpressionSyntax)node, context),
            SyntaxKind.ThrowExpression => TransformThrowExpression((ThrowExpressionSyntax)node, context),
            SyntaxKind.SwitchExpression => TransformSwitchExpression((SwitchExpressionSyntax)node, context),
            SyntaxKind.ThisExpression => "this",
            SyntaxKind.BaseExpression => "super",
            SyntaxKind.ParenthesizedExpression => $"({ExpressionTransformerFacade.Instance.Transform(((ParenthesizedExpressionSyntax)node).Expression, context)})",
            SyntaxKind.ArgListExpression => "/* TODO: __arglist */",
            SyntaxKind.MakeRefExpression or SyntaxKind.RefTypeExpression or SyntaxKind.RefValueExpression => "/* TODO: ref expression */",
            SyntaxKind.WithExpression => "/* TODO: with expression */",
            SyntaxKind.RangeExpression => "/* TODO: range expression */",
            _ => throw new NotSupportedException($"Control flow expression kind {node.Kind()} not supported.")
        };

    private string TransformConditional(ConditionalExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement conditional transformation
        return $"/* TODO: conditional */ {node}";
    }

    private string TransformConditionalAccess(ConditionalAccessExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement conditional access transformation
        return $"/* TODO: conditional access */ {node}";
    }

    private string TransformAwait(AwaitExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement await transformation
        return $"/* TODO: await */ {node}";
    }

    private string TransformThrowExpression(ThrowExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement throw expression transformation
        return $"/* TODO: throw expression */ {node}";
    }

    private string TransformSwitchExpression(SwitchExpressionSyntax node, ConversionContext context)
    {
        // TODO: Implement switch expression transformation
        return $"/* TODO: switch expression */ {node}";
    }
}
