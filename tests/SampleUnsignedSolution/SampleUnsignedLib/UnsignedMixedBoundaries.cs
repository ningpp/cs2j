using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedMixedBoundaries
{
    public static string Run()
    {
        var sb = new StringBuilder();

        ushort s = 65535;
        int i = 1;
        sb.AppendLine((s + i).ToString());

        uint u = 0xFFFFFFFFu;
        sb.AppendLine((u + i).ToString());
        sb.AppendLine((u + 5L).ToString());
        sb.AppendLine((u + 5u).ToString());

        byte b = 255;
        sb.AppendLine((b + i).ToString());
        sb.AppendLine((b + u).ToString());
        sb.AppendLine((b + 1L).ToString());

        ulong ul = 0xFFFFFFFFFFFFFFFFul;
        sb.AppendLine((ul + 5ul).ToString());
        sb.AppendLine((ul + u).ToString());

        sb.AppendLine((u == -1) ? "eq" : "ne");
        sb.AppendLine((u < 0x80000000L) ? "lt" : "ge");
        sb.AppendLine(((int)ul).ToString("X"));
        sb.AppendLine(((long)u).ToString());
        sb.AppendLine(((ulong)u).ToString("X"));
        sb.AppendLine(((byte)u).ToString("X"));
        sb.AppendLine(((double)u).ToString("R"));

        b++;
        s--;
        u += 5;
        ul += 5ul;
        sb.AppendLine(b.ToString());
        sb.AppendLine(s.ToString());
        sb.AppendLine(u.ToString("X"));
        sb.AppendLine(ul.ToString("X"));

        return sb.ToString();
    }
}
