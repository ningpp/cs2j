namespace SampleNumericBoundaryLib;

/// <summary>
/// M2/M3 (NB-1/NB-2/NB-6): every unchecked construct here disappears from the
/// intermediate tree. The runtime behavior after stripping must stay identical
/// because the default project context is unchecked.
/// </summary>
public static class UncheckedSamples
{
    public static int WrapUInt32ToInt32(uint u) => unchecked((int)u);

    public static byte WrapToByte(int v) => unchecked((byte)v);

    public static int WrapAdd(int a, int b) => unchecked(a + b);

    public static int UnwrapBlock(int a, int b)
    {
        int r;
        unchecked
        {
            r = a + b;
            r += 1;
        }

        return r;
    }

    /// <summary>
    /// Nested site: the outer checked block becomes helperized (M2), the inner
    /// unchecked expression wins and is stripped to a plain add.
    /// </summary>
    public static int NestedInsideChecked(int a, int b)
    {
        checked
        {
            int r = unchecked(a + b);
            return r;
        }
    }

    // NB-9: checked shells around decimal arithmetic come off; the call is untouched.
    public static decimal CheckedDecimalAdd(decimal a, decimal b) => checked(a + b);

    // NB-6: folded constant, materialized to a legal long literal (-1L domain).
    public const long U64MaxAsLong = unchecked((long)18446744073709551615UL);
}
