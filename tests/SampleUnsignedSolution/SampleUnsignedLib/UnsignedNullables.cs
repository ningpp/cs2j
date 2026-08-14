using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedNullables
{
    public static string Run()
    {
        var sb = new StringBuilder();
        uint? a = 5u;
        uint? b = null;
        sb.AppendLine(a.HasValue.ToString());
        sb.AppendLine(a.Value.ToString());
        sb.AppendLine(a.GetValueOrDefault().ToString());
        sb.AppendLine((a ?? 0u).ToString());
        sb.AppendLine((b ?? 9u).ToString());
        sb.AppendLine((a == 5u).ToString());
        sb.AppendLine((a == b).ToString());
        sb.AppendLine((a < 10u).ToString());
        sb.AppendLine((a + 1u).ToString());
        sb.AppendLine((b + 1u).ToString());
        return sb.ToString();
    }
}
