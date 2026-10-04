using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// C38 异步释放类型解析（设计 D5/D8、§4.4）。全部判定基于 SemanticModel/ISymbol
/// 符号绑定，禁止按方法名字符串猜测。3a 只覆盖 <c>IAsyncDisposable</c> 面；
/// <c>IAsyncEnumerable/IAsyncEnumerator</c> 判定留 3b，本阶段不实现。
/// </summary>
internal static class AsyncTypeResolver
{
    public const string AsyncDisposableMetadataName = "System.IAsyncDisposable";

    /// <summary>
    /// 解包 <see cref="System.Nullable{T}"/>：<c>S?</c> 资源的 await using 由编译器
    /// 特殊放行（底层 struct 实现接口即可），展开为 <c>if (r.HasValue) await r.Value.DisposeAsync()</c>。
    /// 引用类型可空标注不改变类型符号，不在此处理（守卫以 IsReferenceType 为准）。
    /// </summary>
    public static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type.IsValueType
            && type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return named.TypeArguments[0];
        }

        return type;
    }

    /// <summary>类型是否为 <see cref="System.Nullable{T}"/> 值类型（含 Nullable 解包的判定）。</summary>
    public static bool IsNullableValueType(ITypeSymbol type)
        => type.IsValueType
            && type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>
    /// 类型（含 Nullable 底层类型）是否实现 <c>System.IAsyncDisposable</c>，
    /// 或其本身即该接口。
    /// </summary>
    public static bool IsAsyncDisposable(ITypeSymbol? type, INamedTypeSymbol? asyncDisposableInterface)
    {
        if (type is null || asyncDisposableInterface is null)
            return false;

        var unwrapped = UnwrapNullable(type);
        var comparer = SymbolEqualityComparer.Default;
        return comparer.Equals(unwrapped, asyncDisposableInterface)
            || unwrapped.AllInterfaces.Contains(asyncDisposableInterface, comparer);
    }

    /// <summary>
    /// 类型（含 Nullable 底层类型）是否同时实现 <c>System.IDisposable</c>（D8 双实现查询）。
    /// 双实现不改变 await using 的选择（仍绑 DisposeAsync），只供 Info 记录一次。
    /// </summary>
    public static bool ImplementsSyncDisposable(ITypeSymbol? type, INamedTypeSymbol disposableInterface)
    {
        if (type is null)
            return false;

        var unwrapped = UnwrapNullable(type);
        var comparer = SymbolEqualityComparer.Default;
        return comparer.Equals(unwrapped, disposableInterface)
            || unwrapped.AllInterfaces.Contains(disposableInterface, comparer);
    }

    /// <summary>
    /// 鸭子型异步释放：不实现 <c>IAsyncDisposable</c>，但具有可绑定的无参
    /// <c>DisposeAsync</c> 实例方法且返回 <c>Task/Task&lt;T&gt;/ValueTask/ValueTask&lt;T&gt;</c>。
    /// 含 ref struct 自定义异步释放（ref struct 不能实现接口，只能走模式）。
    /// 3a 对该形态一律 CS2J1842（设计 D8：宁拒不错，与 C06/C44 同边界）。
    /// 调用方须先排除 <see cref="IsAsyncDisposable"/> 成立的类型。
    /// </summary>
    public static bool IsPatternBasedAsyncDisposable(ITypeSymbol? type)
    {
        if (type is null or IErrorTypeSymbol)
            return false;

        var unwrapped = UnwrapNullable(type);
        if (unwrapped.TypeKind == TypeKind.Interface || unwrapped.TypeKind == TypeKind.TypeParameter)
            return false;

        return FindDisposeAsyncMethod(unwrapped) != null;
    }

    /// <summary>
    /// 类型是否具有可直接以 <c>r.DisposeAsync()</c> 调用的公有无参模式方法
    /// （隐式接口实现即满足）。不具备时（如显式接口实现）展开须经
    /// <c>((global::System.IAsyncDisposable)r).DisposeAsync()</c> 转换调用。
    /// 判定基于 Nullable 底层类型，调用接收者由 rewriter 决定是否加 <c>.Value</c>。
    /// </summary>
    public static bool HasAccessiblePatternDispose(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        var unwrapped = UnwrapNullable(type);
        var method = FindDisposeAsyncMethod(unwrapped);
        return method != null
            && method.DeclaredAccessibility == Accessibility.Public
            && method.ExplicitInterfaceImplementations.Length == 0;
    }

    /// <summary>ref struct（IsRefLikeType）资源：v1 与鸭子型同码 CS2J1842 拒绝。</summary>
    public static bool IsRefLike(ITypeSymbol? type)
        => type is not null && type.IsRefLikeType;

    private static IMethodSymbol? FindDisposeAsyncMethod(ITypeSymbol type)
    {
        foreach (var member in type.GetMembers("DisposeAsync"))
        {
            if (member is not IMethodSymbol { IsStatic: false, Parameters.Length: 0 } method)
                continue;
            if (AsyncShapeClassifier.IsTaskLike(method.ReturnType))
                return method;
        }

        return null;
    }
}
