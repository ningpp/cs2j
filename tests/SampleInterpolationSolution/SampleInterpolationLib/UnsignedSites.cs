namespace SampleInterpolationLib;

/// <summary>
/// Unsigned numeric holes: uint/ulong take x.ToString() and byte/ushort take
/// ((uint)x).ToString() in the IE-CONCAT emission so no culture-dependent
/// unsigned overload resolution is relied upon.
/// </summary>
public static class UnsignedSites
{
    public static string UIntPair(uint a, ushort b)
        => $"{a}:{b}";

    public static string ULong(ulong v)
        => $"u={v}";

    public static string BytePair(byte x, byte y)
        => $"{x}/{y}";
}
