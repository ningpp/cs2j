using System;

namespace Cs2jByRef;

/// <summary>
/// 可变位置槽，承接 C# ref struct 形参（Span/ReadOnlySpan 等）。
/// 仅槽模式：ref struct 不能作为数组元素（CS0611），数组模式是死代码（spec Q2-A）。
/// Java 侧映射为 io.github.ningpp.compat.RefStructByRef（普通类）。
/// 注意 T 需要 allows ref struct 反约束（C# 13），否则 Span 不能作为类型实参（CS9244）。
/// </summary>
public ref struct RefStructByRef<T> where T : allows ref struct
{
    private T _slot;

    public RefStructByRef() { _slot = default!; }
    private RefStructByRef(T initial) { _slot = initial; }

    public static RefStructByRef<T> Create() => new();
    public static RefStructByRef<T> Create(T initial) => new(initial);

    public T Value
    {
        get => _slot;
        set => _slot = value;
    }
}
