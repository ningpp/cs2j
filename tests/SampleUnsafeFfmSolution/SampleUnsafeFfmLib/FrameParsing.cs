using System;

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
