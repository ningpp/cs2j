using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedPostfix
{
    public static string Run()
    {
        var sb = new StringBuilder();

        uint a = 5;
        uint r1 = a++;
        sb.AppendLine(r1.ToString());
        sb.AppendLine(a.ToString());
        uint r2 = ++a;
        sb.AppendLine(r2.ToString());
        sb.AppendLine(a.ToString());
        uint r3 = a--;
        sb.AppendLine(r3.ToString());
        sb.AppendLine(a.ToString());
        uint r4 = --a;
        sb.AppendLine(r4.ToString());
        sb.AppendLine(a.ToString());

        uint b = 10;
        uint sum = b++ + b++;
        sb.AppendLine(sum.ToString());
        sb.AppendLine(b.ToString());

        uint c = 1;
        sb.AppendLine(Echo(c++).ToString());
        sb.AppendLine(c.ToString());

        uint[] arr = new uint[] { 100, 200, 300 };
        int idx = 0;
        uint val = arr[idx++];
        sb.AppendLine(val.ToString());
        sb.AppendLine(idx.ToString());

        uint d = 1;
        uint e = ++d * 2;
        sb.AppendLine(d.ToString());
        sb.AppendLine(e.ToString());

        uint f = 1;
        f++;
        ++f;
        f--;
        --f;
        sb.AppendLine(f.ToString());
        return sb.ToString();
    }

    private static uint Echo(uint value) => value;
}
