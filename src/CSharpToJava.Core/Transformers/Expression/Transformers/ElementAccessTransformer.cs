using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles element access expressions (array/index access, index expressions).
/// </summary>
[TransformerRegistration]
public class ElementAccessTransformer : IIRExpressionTransformer
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

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        // For ElementAccessExpression with a single argument
        if (node is ElementAccessExpressionSyntax elemAccess
            && elemAccess.ArgumentList.Arguments.Count == 1)
        {
            var arg = elemAccess.ArgumentList.Arguments[0].Expression;
            // Skip range and from-end — those have special logic
            if (!arg.IsKind(SyntaxKind.RangeExpression) && !arg.IsKind(SyntaxKind.IndexExpression))
            {
                var facade = ExpressionTransformerFacade.Instance;
                var typeInfo = context.GetTypeInfo(elemAccess.Expression);
                var exprType = typeInfo.Type;
                bool isArray = exprType is IArrayTypeSymbol;
                bool isString = exprType?.SpecialType == SpecialType.System_String;

                if (isArray)
                {
                    var targetIR = facade.TransformToIR(elemAccess.Expression, context);
                    var indexIR = facade.TransformToIR(arg, context);
                    return new JavaArrayAccessExpression { Target = targetIR, Index = indexIR };
                }

                // List/Dict/String → JavaMethodCallExpression (get/charAt)
                bool isList = false;
                bool isMap = false;
                string? namedFullName = null;
                if (exprType is INamedTypeSymbol named)
                {
                    namedFullName = named.OriginalDefinition.ToDisplayString();
                    var fullName = namedFullName;
                    isList = fullName is "System.Collections.Generic.List<T>"
                        or "System.Collections.Generic.IList<T>"
                        or "System.Collections.Generic.IReadOnlyList<T>"
                        or "System.Collections.Immutable.ImmutableArray<T>";
                    isMap = fullName is "System.Collections.Generic.Dictionary<TKey, TValue>"
                        or "System.Collections.Generic.IDictionary<TKey, TValue>"
                        or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                        or "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>";
                }

                if (isString || isList || isMap)
                {
                    var targetIR = facade.TransformToIR(elemAccess.Expression, context);
                    var indexIR = facade.TransformToIR(arg, context);
                    var methodName = isString ? "charAt"
                        : (isMap && IsCheckedDictionaryTypeName(namedFullName) ? "getChecked" : "get");
                    var call = new JavaMethodCallExpression { Target = targetIR, MethodName = methodName };
                    call.Arguments.Add(indexIR);
                    return call;
                }

                if (IsJavaStringBuilder(exprType))
                {
                    var targetIR = facade.TransformToIR(elemAccess.Expression, context);
                    var indexIR = facade.TransformToIR(arg, context);
                    var call = new JavaMethodCallExpression { Target = targetIR, MethodName = "charAt" };
                    call.Arguments.Add(indexIR);
                    return call;
                }

                // Pointer type → FFM MemorySegment.get(ValueLayout, offset)
                // 【C06 退役评估 M3b】UnsafeFfmPreprocessor(EnablePointer) 已把指针
                // 元素访问在归一阶段消除；本分支仅服务直连安全网，语句级 FFM 直翻已退役。
                if (exprType is IPointerTypeSymbol)
                {
                    return new JavaRawExpression(Transform(node, context));
                }

                // Unknown type — still try to produce get() call if it's not array-like
                if (exprType != null && exprType.TypeKind != TypeKind.Array)
                {
                    var targetIR = facade.TransformToIR(elemAccess.Expression, context);
                    var indexIR = facade.TransformToIR(arg, context);
                    var call = new JavaMethodCallExpression { Target = targetIR, MethodName = "get" };
                    call.Arguments.Add(indexIR);
                    return call;
                }
            }
        }
        // Fallback to raw expression
        return new JavaRawExpression(Transform(node, context));
    }

    private string TransformElementAccess(ElementAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        if (node.ArgumentList.Arguments.Count == 1)
        {
            var targetExpr = facade.Transform(node.Expression, context);
            var targetType = context.GetTypeInfo(node.Expression).Type;
            if (targetType is IPointerTypeSymbol pointerType)
            {
                // 【C06 退役评估 M3b】C06 归一后仅服务直连安全网；fixed scope
                // 注册零调用（见 ConversionContext.PushFixedScope），语句级 FFM 直翻已退役。
                var pointeeType = pointerType.PointedAtType;
                var elementTypeName = GetPointeeTypeName(pointeeType);
                var pointerInfo = context.FindPointerInfo(targetExpr.Trim());
                if (pointerInfo == null)
                {
                    pointerInfo = FfmHelper.CreatePointerInfo(targetExpr.Trim(), elementTypeName);
                }
                var idxExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                string offsetExpr = pointerInfo.ElementSize == 1
                    ? idxExpr
                    : $"(long)({idxExpr}) * {pointerInfo.ElementSize}";
                // Use base segment with bounds check for pointer element access
                foreach (var imp in FfmHelper.GetRequiredImports(true))
                    context.AddImport(imp);
                if (context.TryGetPointerBase(targetExpr.Trim(), out var baseVar))
                {
                    return FfmHelper.GeneratePointerReadWithBoundsCheck(
                        targetExpr.Trim(), pointerInfo, offsetExpr, baseVar, context);
                }
                return FfmHelper.GeneratePointerRead(targetExpr.Trim(), pointerInfo, offsetExpr);
            }
        }

        var expr = facade.Transform(node.Expression, context);
        // Await receivers ((await helper.Load<T[]>(...))[0]) lose their generic type in
        // Java (join() → Object). Cast the receiver back to the array type so indexing
        // compiles: ((Point_3D[]) x.join())[0]. The await may be wrapped in parens.
        {
            var unwrapped = node.Expression;
            while (unwrapped is ParenthesizedExpressionSyntax parenExpr)
                unwrapped = parenExpr.Expression;
            if (unwrapped is AwaitExpressionSyntax
                && context.SemanticModel != null
                && context.GetTypeInfo(unwrapped).Type is IArrayTypeSymbol awaitArrayType
                && (expr.Trim().EndsWith(".join()", StringComparison.Ordinal)
                    || expr.Trim().EndsWith(".join())", StringComparison.Ordinal)))
            {
                var mappedAwaitArray = context.MapType(awaitArrayType);
                if (!string.IsNullOrWhiteSpace(mappedAwaitArray) && mappedAwaitArray != "Object")
                {
                    context.AddImportsForTypePublic(awaitArrayType.ToDisplayString());
                    expr = $"(({mappedAwaitArray}) {expr})";
                }
            }
        }
        var indexerSymbol = context.GetSymbolInfo(node).Symbol as IPropertySymbol;

        // Determine collection type via semantic model
        var typeInfo = context.GetTypeInfo(node.Expression);
        var exprType = typeInfo.Type;
        // VarTypeMap fallback: when var locals can't be resolved via semantic model
        if ((exprType == null || exprType.TypeKind == TypeKind.Error)
            && node.Expression is IdentifierNameSyntax idExpr
            && context.VarTypeMap.TryGetValue(idExpr.Identifier.Text, out var mappedType)
            && mappedType.TypeKind != TypeKind.Error)
        {
            exprType = mappedType;
        }
        bool isArray = exprType is IArrayTypeSymbol;
        bool isString = exprType?.SpecialType == SpecialType.System_String;
        bool isList = false;
        bool isMap = false;
        string? fullName = null;
        if (exprType is INamedTypeSymbol named)
        {
            fullName = named.OriginalDefinition.ToDisplayString();
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

            if (IsRegexGroupCollection(exprType))
            {
                var groupIndex = facade.Transform(arg, context);
                return $"{expr}.get({groupIndex})";
            }

            // C31 M5: range slicing (arr[lo..hi]) and from-end indexing (arr[^n])
            // are lowered by the index-range preprocessor (default-on in the CLI
            // chain) to ArrayHelper.slice(...) / length-based int indices. The
            // legacy direct arms double-evaluated side-effecting receivers and
            // emitted shapes javac rejects; reaching them means the source was
            // converted without preprocessing.
            if (arg.IsKind(SyntaxKind.RangeExpression))
            {
                throw new NotSupportedException(
                    "CS2J normalization required: range slicing ('..') must be lowered by the index-range preprocessor before Java conversion.");
            }

            if (arg.IsKind(SyntaxKind.IndexExpression))
            {
                throw new NotSupportedException(
                    "CS2J normalization required: from-end index ('^') must be lowered by the index-range preprocessor before Java conversion.");
            }

            var idx = facade.Transform(arg, context);
            var indexType = indexerSymbol?.Parameters.FirstOrDefault()?.Type;
            if (indexType == null && exprType is INamedTypeSymbol namedExpr)
            {
                if (isMap && namedExpr.TypeArguments.Length >= 1)
                    indexType = namedExpr.TypeArguments[0];
                else if (isList)
                    indexType = context.SemanticModel?.Compilation.GetSpecialType(SpecialType.System_Int32);
            }
            // Java arrays require int indices. C# allows long/ulong for array
            // indexing implicitly — narrow with an explicit (int) cast.
            if (isArray && context.SemanticModel != null)
            {
                var argType = context.GetTypeInfo(arg).Type;
                if (argType != null && IsNonIntIntegralType(argType))
                    idx = $"(int)({idx})";
            }
            if (isArray)
            {
                var access = $"{expr}[{idx}]";
                // C# byte (unsigned) maps to Java byte (signed). When reading a byte[] element,
                // mask to preserve unsigned semantics so downstream int arithmetic/comparisons match C#.
                // Skip when the element access is the target of an assignment/ref/out/inc/dec.
                if (!IsAssignmentTarget(node)
                    && exprType is IArrayTypeSymbol arrayType
                    && arrayType.ElementType.SpecialType == SpecialType.System_Byte)
                {
                    // Parenthesized: the mask sits inside binary expressions such
                    // as `expected[i] != actual[i]`, where the bare form would be
                    // parsed as `a & (0xFF != b) & 0xFF`.
                    access = $"({access} & 0xFF)";
                }
                return access;
            }
            if (isString) return $"{expr}.charAt({idx})";
            if (IsJavaStringBuilder(exprType)) return $"{expr}.charAt({idx})";
            if (isMap)
                return IsCheckedDictionaryTypeName(fullName) ? $"{expr}.getChecked({idx})" : $"{expr}.get({idx})";
            if (isList) return $"{expr}.get({idx})";
            // Fallback for unknown/dynamic/unresolved types — check String by display name too
            var typeDisplayName = exprType?.ToDisplayString() ?? "";
            if (typeDisplayName == "string" || typeDisplayName.EndsWith("String"))
                return $"{expr}.charAt({idx})";
            return exprType?.TypeKind == TypeKind.Array ? $"{expr}[{idx}]" : $"{expr}.get({idx})";
        }

        // Multi-argument (e.g., 2D arrays or custom 2D indexers)
        var argList = node.ArgumentList.Arguments.Select((argument, index) =>
        {
            var transformedArg = facade.Transform(argument.Expression, context);
            return transformedArg;
        }).ToList();
        if (isArray)
            return string.Concat(argList.Select(a => $"[{a}]").Prepend(expr));
        else
            return $"{expr}.get({string.Join(", ", argList)})";
    }

    private string TransformFromEndIndex(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        // C31 M5: a standalone ^n (outside an element-access argument) has no
        // lowered form in the converter. The index-range preprocessor reports
        // these as IR120 / pairs them with their use site.
        throw new NotSupportedException(
            "CS2J normalization required: standalone from-end index ('^') must be lowered by the index-range preprocessor before Java conversion.");
    }

    private static bool IsAssignmentTarget(ExpressionSyntax node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax)
            parent = parent.Parent;

        if (parent is AssignmentExpressionSyntax assignment)
            return assignment.Left == node;

        if (parent is ArgumentSyntax argument)
            return argument.RefOrOutKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword;

        if (parent is PrefixUnaryExpressionSyntax prefix)
            return prefix.Operand == node;

        if (parent is PostfixUnaryExpressionSyntax postfix)
            return postfix.Operand == node;

        return false;
    }

    private static bool IsJavaStringBuilder(ITypeSymbol? type)
        => type?.ToDisplayString() == "System.Text.StringBuilder";

    /// <summary>
    /// Returns true for integral types that are not int (C# allows them for array
    /// indexing, but Java requires int). Includes long, ulong, uint, short, ushort,
    /// byte, sbyte, and their unsigned compat wrappers.
    /// </summary>
    private static bool IsNonIntIntegralType(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_UInt32
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Byte
            or SpecialType.System_SByte;

    private static bool IsRegexGroupCollection(ITypeSymbol? type)
        => type?.ToDisplayString() == "System.Text.RegularExpressions.GroupCollection";

    private static string GetPointeeTypeName(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Char => "char",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Boolean => "bool",
            _ => type.Name
        };
    }
    /// <summary>
    /// True for C# generic dictionary types whose Java mapping (CSharpDictionary,
    /// CSharpGenericIDictionary, CSharpReadOnlyDict, ...) exposes a throwing
    /// getChecked(key) mirroring the C# indexer (KeyNotFoundException on missing key).
    /// ConcurrentDictionary maps to java.util.concurrent.ConcurrentHashMap which has
    /// no checked accessor, so it keeps the null-returning get().
    /// </summary>
    private static bool IsCheckedDictionaryTypeName(string? fullName)
    {
        return fullName is "System.Collections.Generic.Dictionary<TKey, TValue>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
    }
}
