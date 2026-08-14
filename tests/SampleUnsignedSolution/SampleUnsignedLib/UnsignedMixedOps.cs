using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedMixedOps
{
    public static string Run()
    {
        var sb = new StringBuilder();
        uint u = 42;
        int i = -5;
        long l = 10;
        ulong ul = 20;

        sb.AppendLine((u + i).ToString());
        sb.AppendLine((u - i).ToString());
        sb.AppendLine((u * i).ToString());
        sb.AppendLine((i + u).ToString());

        sb.AppendLine((u + l).ToString());
        sb.AppendLine((l - u).ToString());

        sb.AppendLine((u + ul).ToString());
        sb.AppendLine((ul - u).ToString());

        sb.AppendLine((u > i).ToString());
        sb.AppendLine((u < l).ToString());
        sb.AppendLine((u == 42L).ToString());

        sb.AppendLine((u + (uint)i).ToString());
        sb.AppendLine((u + (uint)l).ToString());
        return sb.ToString();
    }
}
