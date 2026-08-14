namespace SampleUnsignedLib;

public sealed class UnsignedLiterals
{
    public uint A = 5;
    public uint B = 5u;
    public ulong C = 5ul;
    public uint D = 0xFFFFFFFFu;

    public uint Mixed()
    {
        uint a = 5;
        int b = 3;
        var c = a + (uint)b;
        return c;
    }
}
