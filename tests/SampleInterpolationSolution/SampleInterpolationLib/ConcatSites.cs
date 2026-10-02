namespace SampleInterpolationLib;

public enum SignalColor
{
    Red,
    Green,
    Blue,
}

/// <summary>
/// IE-CONCAT matrix: pure text, empty leading/trailing segments, consecutive
/// holes, a null string hole, bool/enum/double whitelist holes and a mixed
/// site. No collection expressions here (C36 must stay a no-op on this
/// fixture) so every observed change is attributable to C23.
/// </summary>
public static class ConcatSites
{
    public static string PureText()
        => $"plain text";

    public static string Greeting(string name, int n)
        => $"hi {name} #{n}";

    public static string ConsecutiveHoles(int a, int b)
        => $"{a}{b}";

    public static string Brackets(string? s)
        => $"[{s}]";

    public static string NullBrackets()
        => Brackets(null);

    public static string Flag(bool b)
        => $"b={b}";

    public static string ColorName(SignalColor color)
        => $"color={color}";

    public static string DoublePlain(double d)
        => $"d={d}";

    public static string Mixed(string s, int i, bool b)
        => $"{s}/{i}/{b}";
}
