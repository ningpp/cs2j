using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles element access expressions (array/index access, index expressions).
/// </summary>
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
            // Default: use .get() for object types, [] for arrays
            return exprType?.TypeKind == TypeKind.Array ? $"{expr}[{idx}]" : $"{expr}.get({idx})";
        }

        // Multi-argument (e.g., 2D arrays)
        var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));
        return $"{expr}[{args}]";
    }

    private string TransformFromEndIndex(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // ^n as standalone expression — emit a placeholder since we need the collection to compute length
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"/* ^{operand} */(-{operand})";
    }
}
