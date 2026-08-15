namespace SampleCrossCompile;

public static class SharedOps
{
    public static byte Add(byte a, byte b) => (byte)(a + b);
    public static uint Multiply(uint a, uint b) => a * b;
    public static ulong Combine(ulong a, ulong b) => (a << 8) | b;
}
