namespace SampleNumericBoundaryLib;

/// <summary>
/// M2/M3: every built-in integer checked construct here is rewritten to
/// Cs2jUnsigned.UnsignedMath checked helpers. The intermediate tree contains
/// zero checked/unchecked keywords, the host csproj gets a ProjectReference
/// to the released Cs2jUnsignedMath support library, and runtime overflow
/// behavior is preserved (OverflowException on .NET).
/// </summary>
public static class CheckedSamples
{
    // NB-3: checked binary arithmetic across the four domains.
    public static int CheckedAdd(int a, int b) => checked(a + b);

    public static int CheckedSub(int a, int b) => checked(a - b);

    public static int CheckedMul(int a, int b) => checked(a * b);

    public static long CheckedAdd64(long a, long b) => checked(a + b);

    public static uint CheckedAddU32(uint a, uint b) => checked(a + b);

    public static ulong CheckedAddU64(ulong a, ulong b) => checked(a + b);

    // NB-4: checked unary minus (signed domains only).
    public static int CheckedNegate(int a) => checked(-a);

    // -uint promotes to long and can never overflow: shell stripped, plain -a kept.
    public static long CheckedNegateUInt(uint a) => checked(-a);

    // NB-7: checked narrowing conversions.
    public static byte CheckedNarrow(int v) => checked((byte)v);

    public static int CheckedNarrowFromU64(ulong v) => checked((int)v);

    // NB-3/NB-5: checked block with compound accumulation.
    public static int CheckedBlockSum(int[] values)
    {
        int sum = 0;
        checked
        {
            foreach (int v in values)
            {
                sum += v;
            }
        }

        return sum;
    }

    // NB-5: byte compound: arithmetic runs in the promoted Int32 domain and
    // the out-conversion is checked as well.
    public static byte CheckedByteCompound(byte b, byte v)
    {
        checked
        {
            b += v;
        }

        return b;
    }

    // NB-8: prefix increment and postfix consumed in a return.
    public static int CheckedPrefix(int a) => checked(++a);

    public static int CheckedPostfixReturned(int a)
    {
        checked
        {
            return a++;
        }
    }

    // NB-11: enum increment uses the underlying domain and casts back.
    public static CountKind CheckedEnumIncrement(CountKind kind)
    {
        checked
        {
            kind++;
        }

        return kind;
    }
}

public enum CountKind
{
    None = 0,
    One = 1,
    Two = 2,
}
