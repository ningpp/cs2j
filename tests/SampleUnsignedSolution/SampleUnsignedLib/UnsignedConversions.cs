using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedConversions
{
    public static string Run()
    {
        var sb = new StringBuilder();

        uint u = 3000000000u;
        ulong ul = u;
        long l = u;
        float f = u;
        double dbl = u;
        decimal dec = u;
        sb.AppendLine(ul.ToString());
        sb.AppendLine(l.ToString());
        sb.AppendLine(f.ToString("R"));
        sb.AppendLine(dbl.ToString("R"));
        sb.AppendLine(dec.ToString());

        byte b = 5;
        ushort us = 5;
        uint u2 = 5;
        ulong ul2 = 5;
        sb.AppendLine(b.ToString());
        sb.AppendLine(us.ToString());
        sb.AppendLine(u2.ToString());
        sb.AppendLine(ul2.ToString());

        unchecked
        {
            sb.AppendLine(((byte)300u).ToString());
            sb.AppendLine(((ushort)70000u).ToString());
            sb.AppendLine(((byte)ul).ToString());
        }

        sb.AppendLine(default(uint).ToString());
        sb.AppendLine(typeof(uint).Name);
        sb.AppendLine(sizeof(uint).ToString());
        return sb.ToString();
    }
}
