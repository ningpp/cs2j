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
            SyntaxKind.AddressOfExpression => throw NormalizationRequired.At("address-of '&'", node),
            SyntaxKind.PointerIndirectionExpression => throw NormalizationRequired.At("pointer indirection '*'", node),
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
            throw NormalizationRequired.At(node.Kind() == SyntaxKind.AddressOfExpression ? "address-of '&'" : "pointer indirection '*'", node);

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

        if (op is "++" or "--"
            && TryLowerPropertyIncrement(node, operand, op,
                isPostfix: node.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression,
                context, out var propertyLowered))
        {
            return new JavaRawExpression(propertyLowered);
        }

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
        if (TryLowerPropertyIncrement(node, operand, op, isPostfix, context, out var lowered))
            return lowered;
        EnsureSimpleIncrementTarget(operand, context);
        var target = ExpressionTransformerFacade.Instance.Transform(operand, context);
        return isPostfix ? $"{target}{op}" : $"{op}{target}";
    }

    /// <summary>
    /// 属性上的内置数值 ++/-- 不能直译为 Java（getter/setter 是两个方法调用），
    /// 降级为「读一次到临时量 → 写回新值 → 以临时量作为表达式值」，与 C# 编译器
    /// 对属性自增的标准降级同形（一次 get、一次 set）。C09 thread-static 把字段
    /// 站点改写为 holder 属性且链位晚于 numeric-boundary 的增量降级 pass，此缺口
    /// 必须在转换器闭合。范围严格限定为有直接 Java 表示的内置有符号数值类型；
    /// 枚举 / decimal / 无符号窄型 / Nullable 提升 / 自定义运算符仍 fail-fast，
    /// 交对应 C#→C# 预处理器（那些形态需要运算符感知的语义改写）。
    /// </summary>
    private static bool TryLowerPropertyIncrement(
        SyntaxNode unaryNode, ExpressionSyntax operand, string op, bool isPostfix,
        ConversionContext context, out string lowered)
    {
        lowered = "";
        if (operand is not MemberAccessExpressionSyntax ma
            || context.GetSymbolInfo(operand).Symbol is not IPropertySymbol property)
        {
            return false;
        }

        if (context.GetSymbolInfo(unaryNode).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            return false;

        var type = context.GetTypeInfo(operand).Type;
        if (type is null) return false;
        // 键=Java 回写所需的窄化转换（Java 算术统一提升为 int）；null 表示不支持直降。
        var narrowingCast = type.SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_Single or SpecialType.System_Double => "",
            SpecialType.System_SByte => "(byte)",
            SpecialType.System_Int16 => "(short)",
            SpecialType.System_Char => "(char)",
            _ => null,
        };
        if (narrowingCast is null) return false;

        var f = ExpressionTransformerFacade.Instance;
        var receiver = f.Transform(ma.Expression, context) + ".";
        var mapped = context.TypeMappings.MapMethod(
            property.ContainingType.ToDisplayString(), property.Name);
        // 字段风格映射（如 StringBuilder.Length→length）的自增有专门形态
        // （setLength 等），不做通用临时量降级，维持 fail-fast 交预处理器。
        if (mapped != null && !JavaNaming.IsBeanAccessorStyleName(mapped))
            return false;
        var getter = mapped ?? "get" + char.ToUpperInvariant(property.Name[0]) + property.Name[1..];
        var setter = mapped ?? "set" + char.ToUpperInvariant(property.Name[0]) + property.Name[1..];
        var temp = context.GenerateSyntheticName("_inc");
        var arith = op == "++" ? "+" : "-";

        context.AddPreStatement($"var {temp} = {receiver}{getter}();");
        if (isPostfix)
        {
            // 后缀：表达式值为旧值；写回旧值 ±1。
            var updated = narrowingCast.Length == 0
                ? $"{temp} {arith} 1"
                : $"{narrowingCast}({temp} {arith} 1)";
            context.AddPreStatement($"{receiver}{setter}({updated});");
        }
        else
        {
            // 前缀：表达式值为新值；临时量先更新再回写。
            var updated = narrowingCast.Length == 0
                ? $"{temp} {arith} 1"
                : $"{narrowingCast}({temp} {arith} 1)";
            context.AddPreStatement($"{temp} = {updated};");
            context.AddPreStatement($"{receiver}{setter}({temp});");
        }

        lowered = temp;
        return true;
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
            throw NormalizationRequired.At(
                $"logical '!' on non-boolean operand of type '{type.ToDisplayString()}' in {operand.Kind()}", operand);
        }
    }

    /// <summary>+/~/− on user-defined operators or enum operands is not a direct mapping.</summary>
    private static void EnsureBuiltInNumericOperand(ExpressionSyntax node, ExpressionSyntax operand,
        ConversionContext context, bool isBitwiseNot = false)
    {
        var symbolInfo = context.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
        {
            throw NormalizationRequired.At("user-defined unary operator", node);
        }

        var type = context.GetTypeInfo(operand).Type;
        if (type == null) return;
        if (type.TypeKind == TypeKind.Enum)
            throw NormalizationRequired.At($"unary operator on enum operand '{type.ToDisplayString()}'", node);
        if (type.SpecialType == SpecialType.System_Decimal)
            throw NormalizationRequired.At("unary operator on decimal operand", node);
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
                    throw NormalizationRequired.At("increment/decrement on a property or event", operand);
                break;
            case ElementAccessExpressionSyntax:
                if (symbol is IPropertySymbol)
                    throw NormalizationRequired.At("increment/decrement on an indexer", operand);
                break;
            default:
                throw NormalizationRequired.At($"increment/decrement target {operand.Kind()}", operand);
        }

        var type = context.GetTypeInfo(operand).Type;
        if (type == null) return;
        if (type.TypeKind == TypeKind.Enum)
            throw NormalizationRequired.At("increment/decrement on an enum target", operand);
        if (type.SpecialType == SpecialType.System_Decimal)
            throw NormalizationRequired.At("increment/decrement on a decimal target", operand);
        if (type.SpecialType is SpecialType.System_UInt32 or SpecialType.System_UInt64
            or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr)
        {
            throw NormalizationRequired.At($"increment/decrement on unsigned/narrow integer target '{type.ToDisplayString()}'", operand);
        }
        if (symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
            throw NormalizationRequired.At("user-defined increment/decrement operator", operand);
    }

}
