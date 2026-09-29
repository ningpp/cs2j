namespace SamplePatternLib;

/// <summary>
/// Statement-side pattern constructs that the C03 preprocessor must lower into
/// if-chains. Every member's observable behavior is locked by
/// SamplePatternTests so the .NET half verifies semantic equivalence.
/// </summary>
public static class PatternSwitchSamples
{
    public static string ClassifyObject(object? o)
    {
        switch (o)
        {
            case string s when s.Length > 3: return "long-string";
            case string s: return "string";
            case 1 or 2: return "boxed-one-or-two";
            case int i when i > 0: return "positive";
            case int i: return "non-positive-int";
            case null: return "null";
            case bool b: return b ? "true" : "false";
            default: return "other";
        }
    }

    public static string ClassifyBool(bool flag)
    {
        switch (flag)
        {
            case true: return "yes";
            default: return "no";
        }
    }

    public static string ClassifyLong(long value)
    {
        switch (value)
        {
            case 0L: return "zero";
            case 42L: return "answer";
            default: return "other";
        }
    }

    /// <summary>A loop break inside a pattern section must keep its own target.</summary>
    public static int NestedLoopBreakInsidePatternCase(object? o)
    {
        switch (o)
        {
            case int i:
                var total = 0;
                while (true)
                {
                    total++;
                    break;
                }
                total += i;
                return total;
            default: return -1;
        }
    }

    /// <summary>A mid-section break has no if-chain equivalent and must jump past the switch.</summary>
    public static int MidSectionBreak(object? o, bool stopEarly)
    {
        var result = 0;
        switch (o)
        {
            case int i:
                if (stopEarly)
                {
                    result = 100;
                    break;
                }
                result = i;
                break;
            default:
                result = -1;
                break;
        }
        return result + 1;
    }
}

public class Point
{
    public int X { get; set; }
    public int Y { get; set; }
}

public static class PropertyPatternSamples
{
    public static string Describe(Point? p)
    {
        switch (p)
        {
            case Point { X: 0, Y: 0 }: return "origin";
            case Point { X: 0 }: return "y-axis";
            case { X: 1 }: return "x-is-one";
            case null: return "null";
            default: return "other";
        }
    }
}

/// <summary>Cross-file helper: the desugar must not leak bindings across files.</summary>
public static class CrossFileConsumer
{
    public static string Consume(object? o) => "consumed:" + PatternSwitchSamples.ClassifyObject(o);
}
