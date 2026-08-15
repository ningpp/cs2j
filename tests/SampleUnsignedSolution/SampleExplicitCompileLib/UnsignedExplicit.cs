namespace SampleExplicitCompileLib;

public static class UnsignedExplicit
{
    public static byte Add(byte a, byte b) => (byte)(a + b);
    public static uint Multiply(uint a, uint b) => a * b;
}
