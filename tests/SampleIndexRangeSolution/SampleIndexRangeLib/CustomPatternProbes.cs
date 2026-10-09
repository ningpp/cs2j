using System;

namespace SampleIndexRangeLib;

// C31 M4 探针类型：用户自定义 index/range 模式。预处理后所有 ^/.. 使用点
// 一律按 SemanticModel 绑定符号降级为 int 索引/Slice(int,int)
// （this[Index]/this[Range]/Slice(Range) 成员本身保留，作为 C# 端模式
// 表面）。Java 半区暂无 System.Index/System.Range 映射（设计 Q2/§13），
// Java E2E 慢门按文件名排除本文件；其 .NET 运行时语义由 SampleIndexRangeTests
// 在预处理后的中间树上守门。

/// <summary>纯 int 模式：Length + this[int]（IIR 模式降级）。</summary>
public sealed class PatternVec
{
    private readonly int[] _data;

    public PatternVec(params int[] data) => _data = data;

    public int Length => _data.Length;

    public int this[int i] => _data[i];
}

/// <summary>int/System.Index 双索引器：编译器优先绑定 this[Index]，M4 降级回 int 模式。</summary>
public sealed class DualIndexerVec
{
    private readonly int[] _data;

    public DualIndexerVec(params int[] data) => _data = data;

    public int Length => _data.Length;

    public int this[int i] => _data[i];

    public int this[Index i]
    {
        get
        {
            var offset = i.IsFromEnd ? _data.Length - i.Value : i.Value;
            return _data[offset];
        }
    }
}

/// <summary>显式 this[Range] + Slice(int,int)：M4 降级为 Slice 实例调用。</summary>
public sealed class RangeSliceBox
{
    private readonly int[] _data;

    public RangeSliceBox(params int[] data) => _data = data;

    public int Length => _data.Length;

    public int this[int i] => _data[i];

    public int[] this[Range r]
    {
        get
        {
            var start = r.Start.IsFromEnd
                ? _data.Length - r.Start.Value
                : r.Start.Value;
            var end = r.End.IsFromEnd
                ? _data.Length - r.End.Value
                : r.End.Value;
            return Slice(start, end - start);
        }
    }

    public int[] Slice(int offset, int count)
    {
        var result = new int[count];
        Array.Copy(_data, offset, result, 0, count);
        return result;
    }
}

/// <summary>只读视图：Count + this[int] + Slice(int,int)（IIR 模式降级）。</summary>
public readonly struct ReadOnlyListView<T>
{
    private readonly T[] _data;

    public ReadOnlyListView(T[] data) => _data = data;

    public int Count => _data.Length;

    public T this[int i] => _data[i];

    public ReadOnlyListView<T> Slice(int offset, int count)
    {
        var result = new T[count];
        Array.Copy(_data, offset, result, 0, count);
        return new ReadOnlyListView<T>(result);
    }

    public T[] ToArray() => _data;
}

/// <summary>扩展 Slice 接收者：自身只声明 Copy，Slice 全部来自扩展方法。</summary>
public sealed class ExtSliceBox
{
    private readonly int[] _data;

    public ExtSliceBox(params int[] data) => _data = data;

    public int Length => _data.Length;

    public int this[int i] => _data[i];

    public int[] Copy(int offset, int count)
    {
        var result = new int[count];
        Array.Copy(_data, offset, result, 0, count);
        return result;
    }
}

public static class ExtSliceBoxExtensions
{
    public static int[] Slice(this ExtSliceBox box, Range range)
    {
        var start = range.Start.IsFromEnd
            ? box.Length - range.Start.Value
            : range.Start.Value;
        var end = range.End.IsFromEnd
            ? box.Length - range.End.Value
            : range.End.Value;
        return box.Copy(start, end - start);
    }

    public static int[] Slice(this ExtSliceBox box, int offset, int count)
        => box.Copy(offset, count);
}

/// <summary>C31 M4 自定义模式探针的静态使用面（供中间树 xUnit 运行时门调用）。</summary>
public static class CustomPatternOps
{
    public static int PatternLast(PatternVec vector) => vector[^1];

    public static int DualLast(DualIndexerVec vector) => vector[^1];

    public static int[] ExplicitRangeMiddle(RangeSliceBox box) => box[1..^1];

    public static int[] ViewMiddle(ReadOnlyListView<int> view) => view[1..^1].ToArray();

    public static int[] ExtSliceMiddle(ExtSliceBox box) => box.Slice(1..^1);
}
