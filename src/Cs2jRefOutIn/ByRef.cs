using System;

namespace Cs2jByRef;

public abstract class ByRef<T>
{
    public abstract T Value { get; set; }

    // out 临时槽（无 seed）
    public static ByRef<T> Create() => new SlotByRef<T>();
    // ref 临时槽（带 seed）
    public static ByRef<T> Create(T initial) => new SlotByRef<T>(initial);
    // 数组元素别名：读写穿透到 array[index]
    public static ByRef<T> Create(T[] array, int index) => new ArrayElementByRef<T>(array, index);
    // 字段/静态字段/值参成员链别名：访问器闭包，读写穿透
    public static ByRef<T> Create(Func<T> getter, Action<T> setter) => new AccessorByRef<T>(getter, setter);
}

internal sealed class SlotByRef<T> : ByRef<T>
{
    private T _slot;
    public SlotByRef() { _slot = default; }
    public SlotByRef(T initial) { _slot = initial; }
    public override T Value
    {
        get => _slot;
        set => _slot = value;
    }
}

internal sealed class ArrayElementByRef<T> : ByRef<T>
{
    private readonly T[] _array;
    private readonly int _index;
    public ArrayElementByRef(T[] array, int index) { _array = array; _index = index; }
    public override T Value
    {
        get => _array[_index];
        set => _array[_index] = value;
    }
}

internal sealed class AccessorByRef<T> : ByRef<T>
{
    private readonly Func<T> _getter;
    private readonly Action<T> _setter;
    public AccessorByRef(Func<T> getter, Action<T> setter) { _getter = getter; _setter = setter; }
    public override T Value
    {
        get => _getter();
        set => _setter(value);
    }
}
