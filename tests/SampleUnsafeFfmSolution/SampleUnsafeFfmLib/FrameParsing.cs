using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SampleUnsafeFfmLib;

/// <summary>
/// 消息帧拆包示例：struct 内 fixed 大小缓冲区字段（R2 归一为 byte[] 字段）
/// 与 fixed 语句解析报文（R3 归一为数组 + int 索引）。
/// </summary>
public unsafe struct FrameHeader
{
    public const int PayloadCapacity = 256;

    fixed byte payload[PayloadCapacity];
    public int Length;
    public byte Kind;

    public byte GetPayloadByte(int i) => payload[i];

    /// <summary>
    /// 汇总一帧负载：逐字节求和并取末字节（下标与解引用两种指针形态）。
    /// </summary>
    public static int Summarize(byte[] message, out byte last)
    {
        last = 0;
        var sum = 0;

        fixed (byte* p = message)
        {
            for (var i = 0; i < message.Length; i++)
            {
                sum += p[i];
            }

            last = *(p + message.Length - 1);
        }

        return sum;
    }

    /// <summary>fixed 字段经 R2 归一后，直接索引与普通数组字段完全一致。</summary>
    public static int FirstPayloadByte()
    {
        var header = new FrameHeader();
        return header.payload[0];
    }

    /// <summary>
    /// fixed(byte* p = 引用类型内联结构的 fixed 字段) 形态：经 R2+R3 后
    /// p 是数组索引，不再需要钉住宿主对象。
    /// </summary>
    public static int ViaHolder(FrameHolder holder)
    {
        fixed (byte* p = holder.Header.payload)
        {
            return *p;
        }
    }
}

/// <summary>引用类型持有内联 struct：其 fixed 字段必须显式 fixed 才能取址。</summary>
public sealed class FrameHolder
{
    public FrameHeader Header;
}

/// <summary>
/// M2（stackalloc 收编）文本解析场景集合：所有 stackalloc 均为 Span 站点、
/// 元素为可安全堆化的基元；经 R4 后栈缓冲归一为 BCL 堆数组（D12：零新
/// 支撑库），中间树继续可编译。
/// </summary>
public static class FrameText
{
    /// <summary>
    /// 十六进制字符统计：stackalloc 暂存 4 个数字位，典型的 Span/stackalloc
    /// 文本解析形态（R4 → int[]）。
    /// </summary>
    public static int CountHexDigits(ReadOnlySpan<char> text)
    {
        Span<int> digits = stackalloc int[4];
        var used = 0;
        var count = 0;

        foreach (var c in text)
        {
            if (c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            {
                count++;
                digits[used % digits.Length] = c;
                used++;
            }
        }

        return count;
    }

    /// <summary>
    /// 逐字段扫描报文（迭代器方法，C04 会下降为状态机）：方法体被 lowering
    /// 成 MoveNext 状态机，其中的 stackalloc 区段必须在第一次 yield 之前完整
    /// 用完——CS4007 禁止 Span 跨 yield 保留。M2 后 R4 把该栈缓冲归一为堆
    /// 数组，验证两条链互操作后中间树可编译。
    /// </summary>
    public static IEnumerable<int> ScanFieldLengths(byte[] message, int fieldCount)
    {
        Span<int> offsets = stackalloc int[8];
        for (var f = 0; f < offsets.Length; f++) offsets[f] = f * 4;

        var take = Math.Min(fieldCount, offsets.Length);
        var results = new int[take];
        for (var i = 0; i < take; i++)
        {
            var start = offsets[i];
            var end = i + 1 < offsets.Length ? offsets[i + 1] : message.Length;
            results[i] = Math.Max(0, end - start);
        }

        for (var k = 0; k < take; k++)
            yield return results[k];
    }

    /// <summary>
    /// async 方法内使用 stackalloc（CS4007 禁止 Span 跨 await 保留，因此先
    /// await、再使用栈缓冲区段）。R4 堆化后该缓冲在状态机 MoveNext 中即普通
    /// 数组局部。
    /// </summary>
    public static async Task<int> SumHeaderBytesAsync(byte[] message)
    {
        await Task.Yield();

        Span<int> widened = stackalloc int[4];
        var n = Math.Min(widened.Length, message.Length);
        for (var i = 0; i < n; i++) widened[i] = message[i];

        var sum = 0;
        for (var i = 0; i < n; i++) sum += widened[i];
        return sum;
    }

    /// <summary>
    /// goto/label + stackalloc 互操作：eliminate-goto 把方法体重构为结构化
    /// 循环（其 StateMachineBuilder 自带 DowngradeStackAllocAssignments 降级
    /// 形态）；M2 后 stackalloc 已为堆数组，两条规则叠加后中间树必须仍可编译。
    /// </summary>
    public static int GotoScanTotal(byte[] message, int stride)
    {
        Span<int> chunk = stackalloc int[8];
        int total = 0;
        int i = 0;

    restart:
        if (i >= message.Length) return total;
        int slot = i % chunk.Length;
        chunk[slot] = message[i];
        total += chunk[slot];
        i += Math.Max(1, stride);
        goto restart;
    }
}

