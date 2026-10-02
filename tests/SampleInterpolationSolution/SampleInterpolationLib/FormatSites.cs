namespace SampleInterpolationLib;

/// <summary>
/// IE-FORMAT matrix: positive/negative alignment, X and F format clauses and
/// a non-whitelist IFormattable (DateTime) whole-site upgrade. Every site
/// becomes one string.Format(composite, args) call.
/// </summary>
public static class FormatSites
{
    public static string Align(int n)
        => $"[{n,5}|{n,-5}]";

    public static string Hex(byte b)
        => $"0x{b:X2}";

    public static string Fixed2(double d)
        => $"{d:F2}";

    public static string YearMonthDay(System.DateTime dt)
        => $"{dt:yyyy-MM-dd}";
}
