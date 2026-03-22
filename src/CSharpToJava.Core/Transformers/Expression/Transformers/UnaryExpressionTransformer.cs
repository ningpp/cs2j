using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles unary expressions (prefix/postfix operators, address of, pointer indirection).
/// </summary>
[TransformerRegistration]
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
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.SuppressNullableWarningExpression
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
            SyntaxKind.SuppressNullableWarningExpression => ExpressionTransformerFacade.Instance.Transform(((PostfixUnaryExpressionSyntax)node).Operand, context),
            _ => throw new NotSupportedException($"Unary expression kind {node.Kind()} not supported.")
        };

    private string TransformUnaryExpression(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // Check if this is a user-defined unary operator
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedUnaryOperator(node, methodSymbol, context);
                }
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        // Wrap in parentheses if operand is a binary expression
        if (node.Operand is BinaryExpressionSyntax)
        {
            operand = $"({operand})";
        }

        // Special case: unary + is not valid on non-numeric types in Java — strip it
        if (op == "+" && context.SemanticModel != null)
        {
            var operandType = context.SemanticModel.GetTypeInfo(node.Operand).Type;
            if (!IsNumericType(operandType))
                return operand;
        }

        return $"{op}{operand}";
    }

    private static bool IsNumericType(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.SpecialType is
            SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_Int16 or SpecialType.System_Byte or
            SpecialType.System_SByte or SpecialType.System_UInt32 or
            SpecialType.System_UInt64 or SpecialType.System_UInt16 or
            SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_Decimal or SpecialType.System_Char;
    }

    private static bool IsBuiltInType(INamedTypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        return type.TypeKind == TypeKind.Enum ||
               type.SpecialType != SpecialType.None ||
               BuiltInTypeNames.Contains(typeName);
    }

    private static readonly HashSet<string> BuiltInTypeNames = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal",
        "bool", "boolean",
        "char", "string",
        "object",
        "System.Int32", "System.Int64", "System.Int16", "System.Byte",
        "System.SByte", "System.UInt32", "System.UInt64", "System.UInt16",
        "System.Single", "System.Double", "System.Decimal",
        "System.Boolean", "System.Char", "System.String", "System.Object"
    };

    private string TransformUserDefinedUnaryOperator(PrefixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        return TransformUserDefinedUnaryOperatorCore(operand, operatorSymbol, context);
    }

    private string TransformUserDefinedUnaryOperator(PostfixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        return TransformUserDefinedUnaryOperatorCore(operand, operatorSymbol, context);
    }

    private string TransformUserDefinedUnaryOperatorCore(string operand, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        // Get the Java method name for this operator
        var javaMethodName = GetOperatorMethodName(operatorSymbol);
        var containingType = context.MapType(operatorSymbol.ContainingType);
        var currentType = context.CurrentType?.Name;

        if (containingType == currentType)
        {
            return $"{javaMethodName}({operand})";
        }
        else
        {
            return $"{containingType}.{javaMethodName}({operand})";
        }
    }

    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        return operatorSymbol.Name switch
        {
            "op_UnaryNegation" => "negate",
            "op_UnaryPlus" => "plus",
            "op_LogicalNot" => "not",
            "op_OnesComplement" => "onesComplement",
            "op_Increment" => "increment",
            "op_Decrement" => "decrement",
            "op_True" => "isTrue",
            "op_False" => "isFalse",
            _ => operatorSymbol.Name
        };
    }

    private static bool IsInSameCompilationUnit(ConversionContext context, INamedTypeSymbol type)
    {
        var currentNs = context.CurrentNamespace;
        var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";
        return currentNs == typeNs || string.IsNullOrEmpty(typeNs);
    }

    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // C# & operator (address of) has no direct Java equivalent
        context.Diagnostics.Warning("Address-of operator (&) has no Java equivalent - converting to unsafe memory access", node.GetLocation());
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"/* C# addressof — no Java equivalent: {operand} */";
    }

    private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // C# * operator (pointer indirection) has no direct Java equivalent
        context.Diagnostics.Warning("Pointer indirection operator (*) has no Java equivalent - unsafe code not supported", node.GetLocation());
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"/* unsafe: pointer deref */ {operand}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var rewritten))
        {
            return rewritten;
        }

        if (node.Parent is ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var indexerRewrite))
        {
            return indexerRewrite;
        }

        // Check if this is a user-defined postfix operator (++, --)
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedPostfixOperator(node, methodSymbol, context);
                }
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"{operand}{op}";
    }

    private string TransformUserDefinedPostfixOperator(PostfixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        var javaMethodName = GetOperatorMethodName(operatorSymbol);
        var containingType = context.MapType(operatorSymbol.ContainingType);
        var currentType = context.CurrentType?.Name;

        // Postfix semantics: save pre-increment value, apply operator, return saved value
        var tmp = context.GenerateSyntheticName("_post");
        context.AddPreStatement($"var {tmp} = {operand};");
        var methodCall = (containingType == currentType)
            ? $"{operand} = {javaMethodName}({operand});"
            : $"{operand} = {containingType}.{javaMethodName}({operand});";
        context.AddPreStatement(methodCall);
        return tmp;
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var rewritten))
        {
            return rewritten;
        }

        if (node.Parent is ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var indexerRewrite))
        {
            return indexerRewrite;
        }

        // Check if this is a user-defined prefix operator (++, --)
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedUnaryOperator(node, methodSymbol, context);
                }
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"{op}{operand}";
    }

    private static bool TryTransformPropertyIncrementAsSetter(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (context.SemanticModel == null)
            return false;

        var symbol = context.SemanticModel.GetSymbolInfo(operand).Symbol as IPropertySymbol;
        if (symbol == null || symbol.SetMethod == null)
            return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;

        if (operand is MemberAccessExpressionSyntax ma)
        {
            var recv = facade.Transform(ma.Expression, context);
            var propName = symbol.Name;
            var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
            var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
            rewritten = $"{recv}.{setter}({recv}.{getter}() {delta})";
            return true;
        }

        if (operand is IdentifierNameSyntax id)
        {
            var propName = id.Identifier.Text;
            var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
            var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
            rewritten = $"{setter}({getter}() {delta})";
            return true;
        }

        return false;
    }

    private static bool TryTransformIndexerIncrementAsMutation(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (context.SemanticModel == null || operand is not ElementAccessExpressionSyntax ela)
            return false;

        if (ela.ArgumentList.Arguments.Count != 1)
            return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(ela.Expression, context);
        var key = facade.Transform(ela.ArgumentList.Arguments[0].Expression, context);
        var containerType = context.SemanticModel.GetTypeInfo(ela.Expression).Type as INamedTypeSymbol;

        if (containerType != null && IsDictionaryLike(containerType))
        {
            rewritten = $"{target}.put({key}, {target}.get({key}) {delta})";
            return true;
        }

        if (containerType != null && IsListLike(containerType))
        {
            rewritten = $"{target}.set({key}, {target}.get({key}) {delta})";
            return true;
        }

        return false;
    }

    private static bool IsDictionaryLike(INamedTypeSymbol type)
    {
        var fullName = type.OriginalDefinition.ToDisplayString();
        if (fullName is
            "System.Collections.Generic.Dictionary<TKey, TValue>"
            or "System.Collections.Generic.SortedDictionary<TKey, TValue>"
            or "System.Collections.Generic.SortedList<TKey, TValue>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
            or "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>")
            return true;

        return type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>");
    }

    private static bool IsListLike(INamedTypeSymbol type)
    {
        var fullName = type.OriginalDefinition.ToDisplayString();
        if (fullName is
            "System.Collections.Generic.List<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyList<T>")
            return true;

        return type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyList<T>");
    }
}
