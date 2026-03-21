using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles binary expressions (arithmetic, logical, bitwise, comparison, coalesce).
/// </summary>
[TransformerRegistration]
public class BinaryExpressionTransformer : IExpressionTransformer
{
    static BinaryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.AddExpression,
            SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideExpression,
            SyntaxKind.ModuloExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.CoalesceExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.BitwiseOrExpression,
            SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftExpression,
            SyntaxKind.UnsignedRightShiftExpression
        }, new BinaryExpressionTransformer());
    }

    private static readonly Lazy<BinaryExpressionTransformer> _instance = new(() => new());
    public static BinaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.AddExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "+", context),
            SyntaxKind.SubtractExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "-", context),
            SyntaxKind.MultiplyExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "*", context),
            SyntaxKind.DivideExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "/", context),
            SyntaxKind.ModuloExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "%", context),
            SyntaxKind.GreaterThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">", context),
            SyntaxKind.GreaterThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">=", context),
            SyntaxKind.LessThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<", context),
            SyntaxKind.LessThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<=", context),
            SyntaxKind.EqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "==", context),
            SyntaxKind.NotEqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "!=", context),
            SyntaxKind.LogicalAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&&", context),
            SyntaxKind.LogicalOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "||", context),
            SyntaxKind.CoalesceExpression => TransformCoalesceExpression((BinaryExpressionSyntax)node, context),
            SyntaxKind.BitwiseAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&", context),
            SyntaxKind.BitwiseOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "|", context),
            SyntaxKind.ExclusiveOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "^", context),
            SyntaxKind.LeftShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<<", context),
            SyntaxKind.RightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>", context),
            SyntaxKind.UnsignedRightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>>", context),
            _ => throw new NotSupportedException($"Binary expression kind {node.Kind()} not supported.")
        };

    private string TransformBinaryExpression(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Fix: Handle event null comparisons (e.g., ProgressChanged != null)
        // C#: event != null  → Java: !_eventListeners.isEmpty()
        // C#: event == null  → Java: _eventListeners.isEmpty()
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            // Check if this is an event compared to null
            var leftSymbol = context.SemanticModel.GetSymbolInfo(node.Left).Symbol;
            var rightSymbol = context.SemanticModel.GetSymbolInfo(node.Right).Symbol;
            bool rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
            bool leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);

            if ((leftSymbol is IEventSymbol eventSym && rightIsNull) ||
                (rightSymbol is IEventSymbol && leftIsNull))
            {
                var actualEventSym = leftSymbol as IEventSymbol ?? rightSymbol as IEventSymbol;
                var eventName = actualEventSym?.Name;
                var currentTypeName = context.CurrentType?.Name;

                // Only convert to listener access within the same class
                if (eventName != null && currentTypeName != null &&
                    actualEventSym?.ContainingType?.Name == currentTypeName)
                {
                    var fieldName = $"_{char.ToLower(eventName[0])}{eventName.Substring(1)}Listeners";
                    if (op == "!=")
                        return $"!{fieldName}.isEmpty()";
                    else
                        return $"{fieldName}.isEmpty()";
                }
            }
        }

        // Check if this is a user-defined operator that should be converted to a static method call
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type (not built-in types)
                if (methodSymbol.MethodKind == MethodKind.UserDefinedOperator
                    && !IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedOperator(node, methodSymbol, context);
                }
            }
        }

        // Standard operator - use Java's built-in operators
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // String == / != must use .equals() in Java
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            bool leftIsString = IsStringType(node.Left, context.SemanticModel);
            bool rightIsString = IsStringType(node.Right, context.SemanticModel);
            if (leftIsString || rightIsString)
            {
                // null comparisons remain as == / !=
                bool leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);
                bool rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
                if (!leftIsNull && !rightIsNull)
                {
                    string eq = $"{left}.equals({right})";
                    return op == "!=" ? $"!({eq})" : eq;
                }
            }
        }

        // Wrap operands in parentheses when needed for operator precedence
        left = WrapOperandIfNeeded(node.Left, left, op, true);
        right = WrapOperandIfNeeded(node.Right, right, op, false);

        return $"{left} {op} {right}";
    }

    private static bool IsStringType(ExpressionSyntax expr, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(expr);
        return typeInfo.Type?.SpecialType == SpecialType.System_String;
    }

    private static bool IsBuiltInType(INamedTypeSymbol type)
    {
        // Check if the type is a built-in C# type (int, double, string, bool, etc.)
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
        ,"System.Type"
    };

    private string TransformUserDefinedOperator(BinaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Get the Java method name for this operator (e.g., operator+ → add)
        var javaMethodName = GetOperatorMethodName(operatorSymbol);

        // Transform both operands
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // For user-defined operators, we need to call the static method
        // Format: TypeName.method(left, right) or method(left, right) if in same class.
        // Only omit the class qualifier when the call site is inside the operator's own class.
        // Being in the same namespace/package is NOT sufficient — Java requires the class name.
        var currentTypeName = context.CurrentType?.Name;  // e.g. "DemoSet"
        var operatorTypeName = operatorSymbol.ContainingType.Name; // e.g. "DemoSet" (no generics)

        if (currentTypeName != null && operatorTypeName == currentTypeName)
        {
            return $"{javaMethodName}({left}, {right})";
        }
        else
        {
            var containingType = context.MapType(operatorSymbol.ContainingType);
            // Strip type parameters from the class name used as a static call qualifier
            // e.g. "DemoSet<T>" → "DemoSet"; Java doesn't allow type args on static calls.
            var angleIdx = containingType.IndexOf('<');
            if (angleIdx > 0) containingType = containingType[..angleIdx];
            return $"{containingType}.{javaMethodName}({left}, {right})";
        }
    }

    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        // Use the shared operator-name map from OperatorTransformer to avoid divergence
        return CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(operatorSymbol.Name, out var name)
            ? name
            : operatorSymbol.Name;
    }

    private static bool IsInSameCompilationUnit(ConversionContext context, INamedTypeSymbol type)
    {
        // Check if the type is defined in the current namespace/compilation unit
        var currentNs = context.CurrentNamespace;
        var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";

        // Simple heuristic: same namespace means same compilation unit
        return currentNs == typeNs || string.IsNullOrEmpty(typeNs);
    }

    private string WrapOperandIfNeeded(ExpressionSyntax operand, string transformedOperand, string parentOp, bool isLeft)
    {
        // If the operand is a binary expression with lower precedence, wrap in parentheses
        if (operand is BinaryExpressionSyntax)
        {
            var operandKind = operand.Kind();
            var operandPrecedence = GetOperatorPrecedence(operandKind);
            var parentPrecedence = GetOperatorPrecedenceFromToken(parentOp);

            // For left operand, only wrap if operand has lower precedence
            // For right operand, wrap if operand has lower or equal precedence (for right associativity)
            if (isLeft)
            {
                if (operandPrecedence < parentPrecedence) return $"({transformedOperand})";
            }
            else
            {
                if (operandPrecedence <= parentPrecedence) return $"({transformedOperand})";
            }
        }
        return transformedOperand;
    }

    private static int GetOperatorPrecedence(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.CoalesceExpression => 1,
            SyntaxKind.ConditionalExpression => 2,
            SyntaxKind.LogicalOrExpression => 3,
            SyntaxKind.LogicalAndExpression => 4,
            SyntaxKind.BitwiseOrExpression => 5,
            SyntaxKind.ExclusiveOrExpression => 6,
            SyntaxKind.BitwiseAndExpression => 7,
            SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression => 8,
            SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
            SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression => 9,
            SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression or SyntaxKind.UnsignedRightShiftExpression => 10,
            SyntaxKind.AddExpression or SyntaxKind.SubtractExpression => 11,
            SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression => 12,
            _ => 0
        };
    }

    private static int GetOperatorPrecedenceFromToken(string op)
    {
        return op switch
        {
            "||" => 3,
            "&&" => 4,
            "|" => 5,
            "^" => 6,
            "&" => 7,
            "==" or "!=" => 8,
            "<" or "<=" or ">" or ">=" => 9,
            "<<" or ">>" or ">>>" => 10,
            "+" or "-" => 11,
            "*" or "/" or "%" => 12,
            _ => 0
        };
    }

    private string TransformCoalesceExpression(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // Avoid evaluating the left operand twice when it has side effects.
        // Simple identifiers and single-level member accesses are safe to repeat.
        bool isSafeToRepeat = node.Left is IdentifierNameSyntax
            || node.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax };

        if (isSafeToRepeat)
        {
            // C# a ?? b  → Java  a != null ? a : b
            return $"{left} != null ? {left} : {right}";
        }
        else
        {
            // Use a temp variable so the left expression is evaluated only once
            var tmpName = context.GenerateSyntheticName("_coalesce");
            context.AddPreStatement($"var {tmpName} = {left};");
            return $"{tmpName} != null ? {tmpName} : {right}";
        }
    }
}
