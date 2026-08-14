using System;
using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedChecked
{
    public static string Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine(TryChecked(() => { uint x = uint.MaxValue; checked { x++; } return x; }));
        sb.AppendLine(TryChecked(() => { uint x = uint.MaxValue; unchecked { x++; } return x; }));
        sb.AppendLine(TryChecked(() => { uint max = uint.MaxValue; checked { return (int)max; } }));
        return sb.ToString();
    }

    private static string TryChecked(Func<uint> action)
    {
        try { return action().ToString(); }
        catch (OverflowException) { return "overflow"; }
    }

    private static string TryChecked(Func<int> action)
    {
        try { return action().ToString(); }
        catch (OverflowException) { return "overflow"; }
    }
}
