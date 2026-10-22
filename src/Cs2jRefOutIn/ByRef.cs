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

    // ── out 值类型读回（Java 半区 null→default 桥）───────────────────────────
    // C# out 合约保证被调方返回前必然赋值，值类型槽内只可能是写入值或 default(T)，
    // 读回无条件解箱永远安全。Java 泛型擦除下，泛型被调方（如 TryGetValue<T> 的
    // 失败路径 value = default）只能把 null 写进槽里，调用点无条件解箱就会 NPE。
    // 因此值类型 out 拷回经由这些 ReadbackX 发射（RefOutInStatementLifter）：
    // C# 侧无 null 可言，直接透传；Java 半区（io.github.ningpp.compat.ByRef 的
    // readbackX）把 null 映射回对应基本类型的默认值，等价还原 default(T) 语义。
    // 两半区按槽的实例化类型一一对应（C# uint/byte→Java int、ushort→Java short 等
    // 宽化映射已在下方重载中就位）。
    public static int ReadbackInt(ByRef<int> slot) => slot.Value;
    public static int ReadbackInt(ByRef<uint> slot) => (int)slot.Value;
    public static int ReadbackInt(ByRef<byte> slot) => slot.Value;
    public static short ReadbackShort(ByRef<short> slot) => slot.Value;
    public static short ReadbackShort(ByRef<ushort> slot) => (short)slot.Value;
    public static byte ReadbackByte(ByRef<sbyte> slot) => (byte)slot.Value;
    public static long ReadbackLong(ByRef<long> slot) => slot.Value;
    public static long ReadbackLong(ByRef<ulong> slot) => (long)slot.Value;
    public static char ReadbackChar(ByRef<char> slot) => slot.Value;
    public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
    public static float ReadbackFloat(ByRef<float> slot) => slot.Value;
    public static double ReadbackDouble(ByRef<double> slot) => slot.Value;
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

/// <summary>
/// Atomic System.Threading.Interlocked-style operations over <see cref="ByRef{T}"/>
/// proxies. A proxy's <c>Value</c> is a property (often an accessor closure over
/// a field/parameter chain), so the BCL methods cannot take
/// <c>ref proxy.Value</c> (CS0206: a property is not a variable). Every
/// operation locks the proxy instance, which serializes against all other
/// Cs2jInterlocked users of the SAME proxy; the observable semantics
/// (exchange / compare-exchange / CAS arithmetic) are identical to the
/// Interlocked originals. The ref-out-in pass maps
/// <c>Interlocked.X(ref holder, …)</c> here.
/// </summary>
public static class Cs2jInterlocked
{
    public static T Exchange<T>(ByRef<T> slot, T value)
    {
        lock (slot)
        {
            var old = slot.Value;
            slot.Value = value;
            return old;
        }
    }

    public static T CompareExchange<T>(ByRef<T> slot, T value, T comparand)
    {
        lock (slot)
        {
            var old = slot.Value;
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(old, comparand))
                slot.Value = value;
            return old;
        }
    }

    public static int Or(ByRef<int> slot, int value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old | value; return old; }
    }

    public static int And(ByRef<int> slot, int value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old & value; return old; }
    }

    public static int Add(ByRef<int> slot, int value)
    {
        lock (slot) { var updated = slot.Value + value; slot.Value = updated; return updated; }
    }

    public static int Increment(ByRef<int> slot) => Add(slot, 1);
    public static int Decrement(ByRef<int> slot) => Add(slot, -1);

    public static uint Or(ByRef<uint> slot, uint value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old | value; return old; }
    }

    public static uint And(ByRef<uint> slot, uint value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old & value; return old; }
    }

    public static uint Add(ByRef<uint> slot, uint value)
    {
        lock (slot) { var updated = slot.Value + value; slot.Value = updated; return updated; }
    }

    public static uint Increment(ByRef<uint> slot) => Add(slot, 1u);
    public static uint Decrement(ByRef<uint> slot) => Add(slot, 0xFFFFFFFFu);

    public static long Or(ByRef<long> slot, long value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old | value; return old; }
    }

    public static long And(ByRef<long> slot, long value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old & value; return old; }
    }

    public static long Add(ByRef<long> slot, long value)
    {
        lock (slot) { var updated = slot.Value + value; slot.Value = updated; return updated; }
    }

    public static long Increment(ByRef<long> slot) => Add(slot, 1L);
    public static long Decrement(ByRef<long> slot) => Add(slot, -1L);

    public static ulong Or(ByRef<ulong> slot, ulong value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old | value; return old; }
    }

    public static ulong And(ByRef<ulong> slot, ulong value)
    {
        lock (slot) { var old = slot.Value; slot.Value = old & value; return old; }
    }

    public static ulong Add(ByRef<ulong> slot, ulong value)
    {
        lock (slot) { var updated = slot.Value + value; slot.Value = updated; return updated; }
    }

    public static ulong Increment(ByRef<ulong> slot) => Add(slot, 1UL);
    public static ulong Decrement(ByRef<ulong> slot) => Add(slot, 0xFFFFFFFFFFFFFFFFUL);
}

// ref readonly 局部/形参的只读代理（C40 M3 裁决 3）：只暴露 get，对 Value
// 写入在 C# 中间树编译期即 CS0200，只读约束不靠注释维持。堆接收器（数组、
// class/普通 struct 索引器等不被 lambda 捕获限制的位置）用 Func 访问器。
public abstract class ReadOnlyByRef<T>
{
    public abstract T Value { get; }

    // 字段/静态字段/值参成员链只读别名：访问器闭包，读穿透
    public static ReadOnlyByRef<T> Create(Func<T> getter) => new ReadOnlyAccessorByRef<T>(getter);
}

internal sealed class ReadOnlyAccessorByRef<T> : ReadOnlyByRef<T>
{
    private readonly Func<T> _getter;
    public ReadOnlyAccessorByRef(Func<T> getter) { _getter = getter; }
    public override T Value => _getter();
}

// ref struct 接收器（Span<T>/ReadOnlySpan<T>/自定义 ref struct）的「可写元素
// 别名」代理（C40 M3，CS8175 偏差，见 c40-foreach-source-design.md 实施证据节）：
// ref struct 局部/形参不能被 lambda 捕获（CS8175/CS9108），访问器闭包形态
// ByRef<T>.Create(() => span[i], v => span[i] = v) 对 Span 接收器不可编译；
// 本类型以 ref 字段直持元素存储，读/写/透传语义与原生 ref 局部一致，且不引入闭包。
// Java 半区映射到既有 io.github.ningpp.compat.ByRef（Create(Span,int) 工厂内部
// 以 get/set 闭包缝合），不新增 Java 类、不改转换器。
public ref struct ScopedByRef<T>
{
    private ref T _value;

    private ScopedByRef(ref T value) { _value = ref value; }

    public T Value
    {
        get => _value;
        set => _value = value;
    }

    // 通用形态：自定义 ref struct 集合的 ref 索引器等（C# 调用点保留 ref 表达式）
    public static ScopedByRef<T> Create(ref T value) => new ScopedByRef<T>(ref value);

    // Span 形态：调用点无 ref 表达式，Java 可直译为 ByRef.create(span, i)
    public static ScopedByRef<T> Create(Span<T> span, int index) =>
        new ScopedByRef<T>(ref span[index]);
}

// ref struct 接收器的「只读元素别名」代理（ref readonly 局部）：readonly ref
// 字段只暴露 get，对 Value 写入 CS0200。安全 C# 无法把 ref readonly 位置灌入
// readonly ref 字段（CS8331/CS8329），构造点仅此一次 Unsafe.AsRef 强制；字段
// 本身 readonly ref，类型层面不存在写回路径。
public ref struct ReadOnlyScopedByRef<T>
{
    private readonly ref T _value;

    private ReadOnlyScopedByRef(ref readonly T value)
    {
        _value = ref System.Runtime.CompilerServices.Unsafe.AsRef(in value);
    }

    public T Value => _value;

    // 通用形态：ref readonly/ref 索引器（调用点 in 传递，两种返回都接受）
    public static ReadOnlyScopedByRef<T> Create(in T value) =>
        new ReadOnlyScopedByRef<T>(in value);

    // Span/ReadOnlySpan 形态：调用点无 ref 表达式，Java 直译为
    // ReadOnlyByRef.create(span, i)
    public static ReadOnlyScopedByRef<T> Create(Span<T> span, int index) =>
        new ReadOnlyScopedByRef<T>(in span[index]);

    public static ReadOnlyScopedByRef<T> Create(ReadOnlySpan<T> span, int index) =>
        new ReadOnlyScopedByRef<T>(in span[index]);
}
