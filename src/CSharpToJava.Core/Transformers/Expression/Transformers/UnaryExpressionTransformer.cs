using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles unary expressions with the simplest direct C#→Java mapping:
/// +x -x !x ~x, prefix/postfix ++/-- on simple lvalues, and x! (null-forgiving,
/// dropped). Pointer ops, user-defined operators and any non-simple increment
/// target must be lowered by a C#→C# preprocessor and fail fast here.
/// </summary>
[TransformerRegistration]
public class UnaryExpressionTransformer : IIRExpressionTransformer
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
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.SuppressNullableWarningExpression
        }, new UnaryExpressionTransformer());
    }

    private static readonly Lazy<UnaryExpressionTransformer> _instance = new(() => new());
    public static UnaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "+", context),
            SyntaxKind.UnaryMinusExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "-", context),
            SyntaxKind.LogicalNotExpression => TransformLogicalNot((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.BitwiseNotExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "~", context),
            SyntaxKind.AddressOfExpression => throw NormalizationRequired("address-of '&'", node),
            SyntaxKind.PointerIndirectionExpression => throw NormalizationRequired("pointer indirection '*'", node),
            SyntaxKind.PostIncrementExpression => TransformIncrement((PostfixUnaryExpressionSyntax)node, "++", isPostfix: true, context),
            SyntaxKind.PostDecrementExpression => TransformIncrement((PostfixUnaryExpressionSyntax)node, "--", isPostfix: true, context),
            SyntaxKind.PreIncrementExpression => TransformIncrement((PrefixUnaryExpressionSyntax)node, "++", isPostfix: false, context),
            SyntaxKind.PreDecrementExpression => TransformIncrement((PrefixUnaryExpressionSyntax)node, "--", isPostfix: false, context),
            // Null-forgiving operator has no Java counterpart: emit the operand unchanged.
            SyntaxKind.SuppressNullableWarningExpression => ExpressionTransformerFacade.Instance.Transform(GetOperand(node), context),
            _ => throw new NotSupportedException($"Unary expression kind {node.Kind()} not supported.")
        };

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node.Kind() == SyntaxKind.SuppressNullableWarningExpression)
            return ExpressionTransformerFacade.Instance.TransformToIR(GetOperand(node), context);

        if (node.Kind() is SyntaxKind.AddressOfExpression or SyntaxKind.PointerIndirectionExpression)
            throw NormalizationRequired(node.Kind() == SyntaxKind.AddressOfExpression ? "address-of '&'" : "pointer indirection '*'", node);

        var (op, isPostfix) = node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => ("+", false),
            SyntaxKind.UnaryMinusExpression => ("-", false),
            SyntaxKind.LogicalNotExpression => ("!", false),
            SyntaxKind.BitwiseNotExpression => ("~", false),
            SyntaxKind.PostIncrementExpression => ("++", true),
            SyntaxKind.PostDecrementExpression => ("--", true),
            SyntaxKind.PreIncrementExpression => ("++", false),
            SyntaxKind.PreDecrementExpression => ("--", false),
            _ => throw new NotSupportedException($"Unary expression kind {node.Kind()} not supported.")
        };

        var operand = GetOperand(node);

        if (node.Kind() == SyntaxKind.LogicalNotExpression)
            EnsureBooleanOperand(operand, context);
        else if (node.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression
            or SyntaxKind.BitwiseNotExpression)
            EnsureBuiltInNumericOperand(node, operand, context);

        if (op is "++" or "--")
            EnsureSimpleIncrementTarget(operand, context);

        return new JavaUnaryExpression
        {
            Operator = op,
            Operand = ExpressionTransformerFacade.Instance.TransformToIR(operand, context),
            IsPostfix = isPostfix
        };
    }

    private static string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        EnsureBuiltInNumericOperand(node, node.Operand, context);
        return $"{op}{ExpressionTransformerFacade.Instance.Transform(node.Operand, context)}";
    }

    private static string TransformLogicalNot(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        EnsureBooleanOperand(node.Operand, context);
        return $"!{ExpressionTransformerFacade.Instance.Transform(node.Operand, context)}";
    }

    private static string TransformIncrement(SyntaxNode node, string op, bool isPostfix, ConversionContext context)
    {
        var operand = GetOperand(node);
        EnsureSimpleIncrementTarget(operand, context);
        var target = ExpressionTransformerFacade.Instance.Transform(operand, context);
        return isPostfix ? $"{target}{op}" : $"{op}{target}";
    }

    private static ExpressionSyntax GetOperand(SyntaxNode node) => node switch
    {
        PrefixUnaryExpressionSyntax prefix => prefix.Operand,
        PostfixUnaryExpressionSyntax postfix => postfix.Operand,
        _ => throw new NotSupportedException($"Unary node {node.Kind()} has no recognizable operand.")
    };

    /// <summary>! accepts boolean operands only; !int style truthiness must be pre-normalized.</summary>
    private static void EnsureBooleanOperand(ExpressionSyntax operand, ConversionContext context)
    {
        var type = context.GetTypeInfo(operand).Type;
        if (type != null && type.SpecialType != SpecialType.System_Boolean)
        {
            throw NormalizationRequired(
                $"logical '!' on non-boolean operand of type '{type.ToDisplayString()}'", operand);
        }
    }

    /// <summary>+/~/− on user-defined operators or enum operands is not a direct mapping.</summary>
    private static void EnsureBuiltInNumericOperand(ExpressionSyntax node, ExpressionSyntax operand,
        ConversionContext context, bool isBitwiseNot = false)
    {
        var symbolInfo = context.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
        {
            throw NormalizationRequired("user-defined unary operator", node);
        }

        var type = context.GetTypeInfo(operand).Type;
        if (type == null) return;
        if (type.TypeKind == TypeKind.Enum)
            throw NormalizationRequired($"unary operator on enum operand '{type.ToDisplayString()}'", node);
        if (type.SpecialType == SpecialType.System_Decimal)
            throw NormalizationRequired("unary operator on decimal operand", node);
    }

    /// <summary>
    /// ++/-- map directly only for simple mutable lvalues (local/parameter, field,
    /// array element) of built-in numeric types. Everything else requires preprocessing.
    /// </summary>
    private static void EnsureSimpleIncrementTarget(ExpressionSyntax operand, ConversionContext context)
    {
        var symbol = context.GetSymbolInfo(operand).Symbol;
        switch (operand)
        {
            case IdentifierNameSyntax:
                // Locals/parameters are directly incrementable; any other identifier
                // resolves to a field via semantic model below.
                break;
            case MemberAccessExpressionSyntax:
                if (symbol is IPropertySymbol or IEventSymbol)
                    throw NormalizationRequired("increment/decrement on a property or event", operand);
                break;
            case ElementAccessExpressionSyntax:
                if (symbol is IPropertySymbol)
                    throw NormalizationRequired("increment/decrement on an indexer", operand);
                break;
            default:
                throw NormalizationRequired($"increment/decrement target {operand.Kind()}", operand);
        }

        var type = context.GetTypeInfo(operand).Type;
        if (type == null) return;
        if (type.TypeKind == TypeKind.Enum)
            throw NormalizationRequired("increment/decrement on an enum target", operand);
        if (type.SpecialType == SpecialType.System_Decimal)
            throw NormalizationRequired("increment/decrement on a decimal target", operand);
        if (type.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64
            or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr)
        {
            throw NormalizationRequired($"increment/decrement on unsigned/narrow integer target '{type.ToDisplayString()}'", operand);
        }
        if (symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            throw NormalizationRequired("user-defined increment/decrement operator", operand);
    }

    private static NotSupportedException NormalizationRequired(string construct, SyntaxNode node) =>
        new($"CS2J normalization required: {construct} in {node.Kind()} must be lowered by a preprocessor before Java conversion.");
}
