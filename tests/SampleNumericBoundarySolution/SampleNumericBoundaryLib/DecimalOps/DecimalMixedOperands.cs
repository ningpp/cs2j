namespace SampleNumericBoundaryLib.DecimalOps;

/// <summary>
/// C34 DC-9 fixture: integer operands are promoted with explicit
/// <c>(decimal)</c> casts; real uint/ulong operands route through the
/// Cs2jUnsigned.UnsignedMath.ToDecimal* surface (never an unchecked keyword).
/// The checked-region combination itself is covered at unit level; this file
/// deliberately stays free of checked keywords so the dormant (flag-off)
/// chain leaves it byte-identical.
/// </summary>
public static class DecimalMixedOperands
{
    public static decimal SignedAndNarrowIntegers(
        decimal d, int i, short s, byte b, char c, long l, sbyte sb, ushort us)
    {
        var v = d + i;
        v += s;
        v += b;
        v += c;
        v += l;
        v += sb;
        return v + us;
    }

    public static decimal IntegerLiteralsSymmetric(decimal d)
    {
        var a = d + 2;
        var b = 2 * d;
        var c = d + 2u;
        return a + b + c;
    }

    public static decimal RealUInt32(decimal d, uint u) => d + u;

    public static decimal RealUInt64(decimal d, ulong u) => d * u;

    public static decimal MixedAccumulation(decimal d, uint u, int i)
    {
        var fromUnsigned = d * u;
        var fromInteger = d + i;
        return fromUnsigned + fromInteger;
    }
}
