using System;

namespace SampleStructValueSemanticsLib;

/// <summary>字段式事件：SV051 Infeasible（Warning），残余集合显式诊断类型。</summary>
public struct Counter
{
    public event Action? Ticked;

    public int Count;

    public void Tick()
    {
        Count++;
        Ticked?.Invoke();
    }
}

/// <summary>readonly struct：SV012 Info，不参与本 pass。</summary>
public readonly struct ImmutableView
{
    public readonly int V;

    public ImmutableView(int v)
    {
        V = v;
    }

    public int Value => V;
}

/// <summary>ref struct：SV011 Info，交由 C01/C06 处理。</summary>
public ref struct StackBuf
{
    public Span<byte> Bytes;

    public StackBuf(Span<byte> bytes)
    {
        Bytes = bytes;
    }

    public int Length => Bytes.Length;
}
