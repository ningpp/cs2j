using System;

namespace SampleUnsignedLib;

public sealed class UnsignedLambda
{
    public Func<uint, uint> Func = x => x + 1;

    public uint Local(uint a, uint b) => Add(a, b);

    private static uint Add(uint a, uint b) => a + b;
}
