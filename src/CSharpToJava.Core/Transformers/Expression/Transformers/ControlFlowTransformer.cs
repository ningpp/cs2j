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
        var facade = ExpressionTransformerFacade.Instance;
        var condition = facade.Transform(node.Condition, context);
        var trueExpr = facade.Transform(node.WhenTrue, context);
        var falseExpr = facade.Transform(node.WhenFalse, context);
        return $"({condition} ? {trueExpr} : {falseExpr})";
    }

    private string TransformConditionalAccess(ConditionalAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var objExpr = facade.Transform(node.Expression, context);
        var whenNotNull = facade.TransformWhenNotNull(node.WhenNotNull, objExpr, context);
        return $"({objExpr} != null ? {whenNotNull} : null)";
    }

    private string TransformAwait(AwaitExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expr = facade.Transform(node.Expression, context);
        // Map C# await → CompletableFuture.join()
        return $"{expr}.join()";
    }

    private string TransformThrowExpression(ThrowExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expr = facade.Transform(node.Expression, context);
        // Java has no throw expressions; wrap in a Supplier lambda (works for unchecked exceptions)
        return $"((java.util.function.Supplier<Object>) () -> {{ throw {expr}; }}).get()";
    }

    private string TransformSwitchExpression(SwitchExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var governingExpr = facade.Transform(node.GoverningExpression, context);

        // Build a ternary chain from last arm to first
        string result = "null";
        foreach (var arm in node.Arms.Reverse())
        {
            var armValue = facade.Transform(arm.Expression, context);
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause == null)
            {
                result = armValue;
            }
            else
            {
                var condition = BuildSwitchArmCondition(governingExpr, arm.Pattern, context);
                if (arm.WhenClause != null)
                    condition = $"({condition}) && ({facade.Transform(arm.WhenClause.Condition, context)})";
                result = $"({condition} ? {armValue} : {result})";
            }
        }
        return result;
    }

    private string BuildSwitchArmCondition(string expr, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                => $"({expr} == null)",
            ConstantPatternSyntax cp
                => $"java.util.Objects.equals({expr}, {facade.Transform(cp.Expression, context)})",
            DeclarationPatternSyntax dp
                => BuildDeclarationPatternCondition(expr, dp, context),
            TypePatternSyntax tp
                => $"({expr} instanceof {context.MapTypeFromSyntax(tp.Type)})",
            DiscardPatternSyntax
                => "true",
            UnaryPatternSyntax np when np.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => $"!({BuildSwitchArmCondition(expr, np.Pattern, context)})",
            _ => $"/* TODO: pattern {pattern.GetType().Name} */ true"
        };
    }

    private string BuildDeclarationPatternCondition(string expr, DeclarationPatternSyntax dp, ConversionContext context)
    {
        var mappedType = context.MapTypeFromSyntax(dp.Type);
        var designation = dp.Designation switch
        {
            SingleVariableDesignationSyntax sv => ConversionContext.EscapeJavaKeyword(sv.Identifier.Text),
            DiscardDesignationSyntax => "_",
            _ => "_unused"
        };
        return $"({expr} instanceof {mappedType} {designation})";
    }
}
