using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedPatterns
{
    public static string Run()
    {
        var sb = new StringBuilder();
        uint u = 7;
        sb.AppendLine(Switch1(u));
        sb.AppendLine(Switch2(u));
        sb.AppendLine(SwitchExpr(u));
        sb.AppendLine(Patterns(u));
        sb.AppendLine(Relational(u));
        return sb.ToString();
    }

    private static string Switch1(uint value)
    {
        switch (value)
        {
            case 1u: return "one";
            case uint.MaxValue: return "max";
            case 7u: return "seven";
            default: return "other";
        }
    }

    private static string Switch2(uint value) => value switch
    {
        1u => "one",
        > 5u and < 10u => "mid",
        uint.MaxValue => "max",
        _ => "other",
    };

    private static string SwitchExpr(uint value) => value switch
    {
        7u => "seven",
        _ => "default",
    };

    private static string Patterns(uint value)
    {
        if (value is > 5u and < 10u) return "5-10";
        if (value is 7u) return "seven";
        return "no";
    }

    private static string Relational(uint value)
    {
        if (value > 5u && value <= 7u) return "5<v<=7";
        return "no";
    }
}
