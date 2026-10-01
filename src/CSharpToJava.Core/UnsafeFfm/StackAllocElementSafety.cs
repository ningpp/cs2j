using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R4 stackalloc 语义事实（共享单一真相，供预处理前置拒绝预检与
/// <see cref="StackAllocRewriter"/> 共同使用，避免判定分叉）。
/// 全部基于 <see cref="SemanticModel"/>，不做文本匹配。
/// </summary>
internal static class StackAllocElementSafety
{
    /// <summary>
    /// 站点类型：节点 GetTypeInfo 的 ConvertedType（目标类型转换），回落到 Type。
    /// </summary>
    public static ITypeSymbol? GetSiteType(SemanticModel model, ExpressionSyntax node)
    {
        var info = model.GetTypeInfo(node);
        return info.ConvertedType ?? info.Type;
    }

    /// <summary>显式形态元素符号：ArrayType.ElementType 在模型中的类型。</summary>
    public static ITypeSymbol? GetExplicitElement(
        SemanticModel model, StackAllocArrayCreationExpressionSyntax node)
    {
        if (node.Type is ArrayTypeSyntax arrayType)
        {
            var info = model.GetTypeInfo(arrayType.ElementType);
            if (info.Type != null) return info.Type;
        }

        return null;
    }

    /// <summary>
    /// 隐式形态元素符号：站点 Span&lt;T&gt;/T*/T[] 的参数 T；回落到首个
    /// 初始化表达式的类型。
    /// </summary>
    public static ITypeSymbol? GetImplicitElement(
        SemanticModel model, ImplicitStackAllocArrayCreationExpressionSyntax node)
    {
        var siteType = GetSiteType(model, node);
        if (TryGetSpanElement(siteType, out var element) && element != null) return element;
        if (siteType is IArrayTypeSymbol array) return array.ElementType;
        if (siteType is IPointerTypeSymbol pointer) return pointer.PointedAtType;

        if (node.Initializer.Expressions.Count > 0)
        {
            var info = model.GetTypeInfo(node.Initializer.Expressions[0]);
            return info.Type;
        }

        return null;
    }

    /// <summary>
    /// 元素是否可安全堆化。直接元素允许全部 unmanaged 基元（含 bool/char，
    /// 文本解析场景必需）、枚举；struct 要求按经典互操作封送规则可封送
    /// （blittable：所有非静态实例字段递归可封送，bool/char 字段使 struct
    /// 非可封送）；类型参数、指针、数组/引用类型一律拒绝。
    /// </summary>
    public static bool IsSafeElement(ITypeSymbol type, out string reason)
    {
        reason = string.Empty;

        switch (type)
        {
            case ITypeParameterSymbol:
                reason = "the element is an open generic type parameter (heap array of T is not " +
                         "provably equivalent here).";
                return false;

            case IPointerTypeSymbol:
                reason = "the element itself is a pointer type.";
                return false;

            case IArrayTypeSymbol:
                reason = "the element itself is an array type.";
                return false;
        }

        if (type.IsReferenceType)
        {
            reason = "the element is a reference type.";
            return false;
        }

        if (IsAllowedElementPrimitive(type) || type.TypeKind == TypeKind.Enum)
            return true;

        if (type.IsTupleType)
        {
            reason = "the element is a tuple type (not a marshallable struct form).";
            return false;
        }

        if (type is INamedTypeSymbol { IsValueType: true } structType)
        {
            if (IsBlittableStruct(structType, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)))
                return true;
            reason = "the struct is not blittable/marshallable (it contains a bool/char field or " +
                     "another non-blittable member).";
            return false;
        }

        reason = "the element type is not a recognized unmanaged value type.";
        return false;
    }

    /// <summary>
    /// 直接元素基元白名单：所有非托管预定义类型（含 bool、char、IntPtr/UIntPtr）。
    /// </summary>
    private static bool IsAllowedElementPrimitive(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Char
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr;

    /// <summary>
    /// 经典 blittable struct 判定：所有非静态实例字段（含私有/编译器生成支持
    /// 字段）递归均为 blittable。字段级 blittable 基元不含 bool/char（与
    /// .NET 互操作封送规则一致），枚举与嵌套 blittable struct 允许。
    /// </summary>
    private static bool IsBlittableStruct(INamedTypeSymbol structType, HashSet<INamedTypeSymbol> visiting)
    {
        if (!visiting.Add(structType)) return false;

        foreach (var member in structType.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic) continue;
            var fieldType = field.Type;

            if (fieldType.TypeKind == TypeKind.Enum) continue;
            if (IsBlittableFieldPrimitive(fieldType)) continue;

            // 仅对「无特殊类型的用户 struct」递归——bool/char 等 BCL 命名值类型
            // （SpecialType 非 None）不是用户 struct，直接判非 blittable；
            // 否则会把 bool 本身递归进去（其无可见实例字段而误判为 true）。
            if (fieldType is INamedTypeSymbol
                {
                    IsValueType: true,
                    TypeKind: TypeKind.Struct,
                    SpecialType: SpecialType.None,
                    IsTupleType: false,
                } nested
                && IsBlittableStruct(nested, visiting))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsBlittableFieldPrimitive(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr;

    /// <summary>是否 System.Span&lt;T&gt;/System.ReadOnlySpan&lt;T&gt;，输出元素类型参数。</summary>
    public static bool IsSpanLike(ITypeSymbol? type, out ITypeSymbol? elementType)
    {
        elementType = null;
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.TypeArguments.Length == 1)
        {
            var definition = named.OriginalDefinition.ToDisplayString();
            if (definition is "System.Span<T>" or "System.ReadOnlySpan<T>")
            {
                elementType = named.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSpanElement(ITypeSymbol? type, out ITypeSymbol? elementType)
    {
        if (IsSpanLike(type, out elementType)) return true;
        elementType = null;
        return false;
    }
}
