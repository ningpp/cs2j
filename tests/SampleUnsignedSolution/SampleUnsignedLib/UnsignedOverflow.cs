using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedOverflow
{
    public static string Run()
    {
        var sb = new StringBuilder();
        unchecked
        {
            uint u = uint.MaxValue;
            sb.AppendLine((u + 1u).ToString());
            sb.AppendLine((u + 2u).ToString());
            sb.AppendLine((u * 2u).ToString());
            sb.AppendLine((u - 1u).ToString());

            ulong ul = ulong.MaxValue;
            sb.AppendLine((ul + 1ul).ToString());
            sb.AppendLine((ul * 2ul).ToString());

            byte b = 255;
            sb.AppendLine(((byte)(b + 1)).ToString());
            sb.AppendLine(((byte)(b + 2)).ToString());

            uint c = uint.MaxValue;
            c++;
            sb.AppendLine(c.ToString());
            c--;
            sb.AppendLine(c.ToString());
            ++c;
            sb.AppendLine(c.ToString());

            uint d = uint.MaxValue;
            d += 2u;
            sb.AppendLine(d.ToString());
            d *= 2u;
            sb.AppendLine(d.ToString());

            sb.AppendLine(((int)uint.MaxValue).ToString());
            sb.AppendLine(((int)ulong.MaxValue).ToString());
            sb.AppendLine(((short)65535u).ToString());
            sb.AppendLine(((sbyte)255u).ToString());
            sb.AppendLine(((byte)256u).ToString());
            sb.AppendLine(((uint)(-1)).ToString());
            sb.AppendLine(((long)uint.MaxValue).ToString());
            sb.AppendLine(((long)ulong.MaxValue).ToString());
        }

        return sb.ToString();
    }
}
