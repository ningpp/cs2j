using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedComplex
{
    public static string Run()
    {
        var sb = new StringBuilder();
        uint a = 7;
        uint b = 3;
        sb.AppendLine((a + b * 2).ToString());
        sb.AppendLine(((a + b) * 2).ToString());
        sb.AppendLine((a - b).ToString());
        sb.AppendLine((a / b).ToString());
        sb.AppendLine((a % b).ToString());
        sb.AppendLine((a & b).ToString());
        sb.AppendLine((a | b).ToString());
        sb.AppendLine((a ^ b).ToString());
        sb.AppendLine((~a).ToString());
        sb.AppendLine((a << 3).ToString());
        sb.AppendLine((a >> 1).ToString());
        sb.AppendLine((a == b).ToString());
        sb.AppendLine((a != b).ToString());
        sb.AppendLine((a < b).ToString());
        sb.AppendLine((a <= b).ToString());
        sb.AppendLine((a > b).ToString());
        sb.AppendLine((a >= b).ToString());

        sb.AppendLine((a + 1).ToString());
        sb.AppendLine((1 + a).ToString());
        sb.AppendLine((a * 2 + 1).ToString());
        sb.AppendLine(((a + b) * (a - b)).ToString());

        byte x = 200;
        byte y = 100;
        sb.AppendLine((x + y).ToString());
        sb.AppendLine((x - y).ToString());
        sb.AppendLine((x * y).ToString());
        ushort us = 40000;
        sb.AppendLine((us + us).ToString());
        sb.AppendLine((~x).ToString());
        sb.AppendLine((x << 2).ToString());

        ulong ua = 100;
        ulong ub = 3;
        sb.AppendLine((ua / ub).ToString());
        sb.AppendLine((ua % ub).ToString());
        sb.AppendLine((ua << 2).ToString());

        int n = 5;
        sb.AppendLine((a + n).ToString());
        sb.AppendLine((n + a).ToString());
        sb.AppendLine((a * n).ToString());
        sb.AppendLine((a - n).ToString());
        sb.AppendLine((n - a).ToString());
        sb.AppendLine((a < n).ToString());
        sb.AppendLine((n > a).ToString());
        return sb.ToString();
    }
}
