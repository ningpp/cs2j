namespace SampleNumericBoundaryLib.IncrementTargets;

/// <summary>
/// C17 M1 enum/unsigned increment fixture: simple enum locals, enum array
/// elements and simple unsigned residuals are owned outside checked regions
/// and rewritten to a single-evaluation read-modify-write shape with explicit
/// domain casts (uint/ulong go through Cs2jUnsigned.UnsignedMath helpers).
/// </summary>
public enum IncrementColor : byte
{
    Red,
    Green,
    Blue,
    Max = 255,
}

public enum IncrementRank
{
    Zero,
    One,
    Two,
}

public static class EnumIncrementSamples
{
    // IT-6: int-backed simple enum local uses the (E)(int)((int)e ± 1) core.
    public static IncrementRank SimpleEnumLocal()
    {
        var rank = IncrementRank.Zero;
        rank++;
        ++rank;
        return rank;
    }

    // byte-backed enum wraps naturally through the narrow write-back cast:
    // (E)(byte)((byte)e + 1) truncates 256 back to 0.
    public static IncrementColor ByteEnumWraps()
    {
        var color = IncrementColor.Max;
        color++;
        return color;
    }

    // IT-4: enum array element; the index expression is hoisted so it is
    // evaluated exactly once; postfix still observes the old value.
    public static IncrementColor EnumArrayElementPostfix(IncrementColor[] colors, int index)
    {
        IncrementColor old = colors[index]++;
        return old;
    }

    public static IncrementColor EnumArrayElementPrefix(IncrementColor[] colors, int index)
    {
        return ++colors[index];
    }

    // IT-5: unsigned residuals. uint/ulong route through the support-library
    // helpers; byte/ushort use promoted Int32 arithmetic plus a narrow cast.
    public static uint UIntResidualRoundTrip(uint seed)
    {
        uint u = seed;
        u++;
        u--;
        return u;
    }

    public static ulong ULongResidualIncrement(ulong seed)
    {
        ulong u = seed;
        u++;
        return u;
    }

    public static byte ByteResidualIncrement(byte seed)
    {
        byte b = seed;
        b++;
        return b;
    }

    public static ushort UShortResidualIncrement(ushort seed)
    {
        ushort s = seed;
        s++;
        return s;
    }

    public static byte ByteResidualWraps()
    {
        byte b = byte.MaxValue;
        b++;
        return b;
    }

    public static uint UIntResidualWraps()
    {
        uint u = uint.MaxValue;
        u++;
        return u;
    }
}
