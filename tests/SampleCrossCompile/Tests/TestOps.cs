namespace SampleCrossCompile;

public static class TestOps
{
    public static uint Add(uint a, uint b) => a + b;
    public static ulong Combine(ulong a, ulong b) => (a << 8) | b;
}
