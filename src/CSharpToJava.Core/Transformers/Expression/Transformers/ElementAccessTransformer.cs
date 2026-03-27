using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles element access expressions (array/index access, index expressions).
/// </summary>
[TransformerRegistration]
public class ElementAccessTransformer : IExpressionTransformer
{
    static ElementAccessTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.IndexExpression
        }, new ElementAccessTransformer());
    }

    private static readonly Lazy<ElementAccessTransformer> _instance = new(() => new());
    public static ElementAccessTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ElementAccessExpression => TransformElementAccess((ElementAccessExpressionSyntax)node, context),
            SyntaxKind.IndexExpression => TransformFromEndIndex((PrefixUnaryExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Element access kind {node.Kind()} not supported.")
        };

    private string TransformElementAccess(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expr = facade.Transform(node.Expression, context);

        // Determine collection type via semantic model
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.Expression);
        var exprType = typeInfo?.Type;
        bool isArray = exprType is IArrayTypeSymbol;
        bool isString = exprType?.SpecialType == SpecialType.System_String;
        bool isList = false;
        bool isMap = false;
        if (exprType is INamedTypeSymbol named)
        {
            var fullName = named.OriginalDefinition.ToDisplayString();
            isList = fullName is "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Immutable.ImmutableArray<T>";
            isMap = fullName is "System.Collections.Generic.Dictionary<TKey, TValue>"
                or "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                or "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>";
        }

        // Single argument (most common case)
        if (node.ArgumentList.Arguments.Count == 1)
        {
            var arg = node.ArgumentList.Arguments[0].Expression;

            // Fix 2 companion: handle arr[lo..hi] range slicing directly here so the result
            // is not double-wrapped by the general [idx] path below.
            if (arg.IsKind(SyntaxKind.RangeExpression) && arg is RangeExpressionSyntax range)
            {
                var lo = range.LeftOperand != null ? facade.Transform(range.LeftOperand, context) : "0";
                var hi = range.RightOperand != null ? facade.Transform(range.RightOperand, context)
                    : isArray ? $"{expr}.length" : $"{expr}.size()";
                context.AddImport("java.util.Arrays");
                return $"Arrays.copyOfRange({expr}, {lo}, {hi})";
            }

            if (arg.IsKind(SyntaxKind.IndexExpression) && arg is PrefixUnaryExpressionSyntax fromEnd)
            {
                // C# ^n (index from end)
                var operand = facade.Transform(fromEnd.Operand, context);
                if (isArray)
                    return $"{expr}[{expr}.length - {operand}]";
                if (isString)
                    return $"{expr}.charAt({expr}.length() - {operand})";
                return $"{expr}.get({expr}.size() - {operand})";
            }

            var idx = facade.Transform(arg, context);
            if (isArray) return $"{expr}[{idx}]";
            if (isString) return $"{expr}.charAt({idx})";
            if (isMap) return $"{expr}.get({idx})";
            if (isList) return $"{expr}.get({idx})";
            // Fallback for unknown/dynamic/unresolved types — check String by display name too
            var typeDisplayName = exprType?.ToDisplayString() ?? "";
            if (typeDisplayName == "string" || typeDisplayName.EndsWith("String"))
                return $"{expr}.charAt({idx})";
            return exprType?.TypeKind == TypeKind.Array ? $"{expr}[{idx}]" : $"{expr}.get({idx})";
        }

        // Multi-argument (e.g., 2D arrays or custom 2D indexers)
        var argList = node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)).ToList();
        if (isArray)
            return string.Concat(argList.Select(a => $"[{a}]").Prepend(expr));
        else
            return $"{expr}.get({string.Join(", ", argList)})";
    }

    private string TransformFromEndIndex(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // ^n as standalone expression — no direct Java equivalent; emit a comment only
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"/* C# from-end index ^{operand} — requires array name to resolve */";
    }
}
