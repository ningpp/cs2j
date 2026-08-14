namespace SampleUnsignedLib;

public sealed class UnsignedOperators
{
    public uint Add(uint a, uint b) => a + b;
    public uint Subtract(uint a, uint b) => a - b;
    public uint Multiply(uint a, uint b) => a * b;
    public uint Divide(uint a, uint b) => a / b;
    public uint Remainder(uint a, uint b) => a % b;
    public uint And(uint a, uint b) => a & b;
    public uint Or(uint a, uint b) => a | b;
    public uint Xor(uint a, uint b) => a ^ b;
    public uint Not(uint a) => ~a;
    public uint LeftShift(uint a, int n) => a << n;
    public uint RightShift(uint a, int n) => a >> n;
    public bool Equal(uint a, uint b) => a == b;
    public bool NotEqual(uint a, uint b) => a != b;
    public bool Less(uint a, uint b) => a < b;
    public bool LessOrEqual(uint a, uint b) => a <= b;
    public bool Greater(uint a, uint b) => a > b;
    public bool GreaterOrEqual(uint a, uint b) => a >= b;

    public void Mutate(ref uint a, uint b)
    {
        a++;
        a--;
        ++a;
        --a;
        a += b; a -= b; a *= b; a /= b; a %= b;
        a &= b; a |= b; a ^= b;
    }
}
