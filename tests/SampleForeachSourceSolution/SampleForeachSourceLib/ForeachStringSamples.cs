using System.Collections.Generic;

namespace SampleForeachSourceLib;

/// <summary>
/// C16 foreach-source fixture samples. Every behavior here must stay
/// runtime-identical after FS-1 wraps string foreach sites with
/// ToCharArray() (and ruling-6 inserts char temp loops for explicit
/// non-char element types).
/// </summary>
public sealed class ForeachStringSamples
{
    private string _field = "xy";
    private string Property => "pq";

    /// <summary>Parameter source.</summary>
    public static List<char> Collect(string s)
    {
        var result = new List<char>();
        foreach (var c in s)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>Field/property/literal/concat sources in one method.</summary>
    public List<char> CollectAllKinds(string param)
    {
        var result = new List<char>();
        foreach (var c in param)
        {
            result.Add(c);
        }
        foreach (var c in _field)
        {
            result.Add(c);
        }
        foreach (var c in Property)
        {
            result.Add(c);
        }
        foreach (var c in "lit")
        {
            result.Add(c);
        }
        foreach (var c in "a" + param)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>char element stays a plain char variable.</summary>
    public static List<char> ExplicitChar(string s)
    {
        var result = new List<char>();
        foreach (char c in s)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>Ruling 6: explicit int element gets char temp + cast; values are UTF-16 code units.</summary>
    public static List<int> CodeUnits(string s)
    {
        var result = new List<int>();
        foreach (int c in s)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>Ruling 6: boxing conversion (object) element.</summary>
    public static List<object> BoxedChars(string s)
    {
        var result = new List<object>();
        foreach (object c in s)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>Nested loops: outer source is string[] (out of scope), inner source is string (wrapped).</summary>
    public static List<char> Nested(string[] rows)
    {
        var result = new List<char>();
        foreach (var row in rows)
        {
            foreach (var c in row)
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>Non-string sources must be left exactly as written.</summary>
    public static List<char> NonStringSites(List<char> list, char[] array)
    {
        var result = new List<char>();
        foreach (var c in list)
        {
            result.Add(c);
        }
        foreach (var c in array)
        {
            result.Add(c);
        }
        return result;
    }

    /// <summary>Empty string iterates zero times.</summary>
    public static int EmptyIterations(string s)
    {
        var count = 0;
        foreach (var c in s)
        {
            count++;
        }
        return count;
    }

    /// <summary>Null argument: NRE timing must stay at loop entry.</summary>
    public static void ThrowOnNullParameter(string? s)
    {
        foreach (var c in s!)
        {
            _ = c;
        }
    }

    /// <summary>Null returned by a method call: NRE timing must stay at loop entry.</summary>
    public static void ThrowOnNullReturn()
    {
        foreach (var c in GetNullText())
        {
            _ = c;
        }
    }

    private static string GetNullText() => null!;

    private int _getTextCalls;
    public int GetTextCallCount => _getTextCalls;

    /// <summary>The source expression must still be evaluated exactly once.</summary>
    public List<char> SingleEvaluation()
    {
        _getTextCalls = 0;
        var result = new List<char>();
        foreach (var c in GetTextWithCount())
        {
            result.Add(c);
        }
        return result;
    }

    private string GetTextWithCount()
    {
        _getTextCalls++;
        return "once";
    }
}
