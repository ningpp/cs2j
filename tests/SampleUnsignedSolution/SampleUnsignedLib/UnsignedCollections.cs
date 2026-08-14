using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedCollections
{
    public static string Run()
    {
        var sb = new StringBuilder();
        var list = new List<uint> { 1u, 2u, 3u };
        list.Add(4u);
        sb.AppendLine(list.Count.ToString());
        sb.AppendLine(list.Aggregate((x, y) => x + y).ToString());
        sb.AppendLine(list.Max().ToString());
        sb.AppendLine(list.Min().ToString());
        sb.AppendLine(string.Join(",", list.Select(x => x + 1u)));

        var dict = new Dictionary<uint, string> { [1u] = "one", [2u] = "two" };
        sb.AppendLine(dict[1u]);

        var hs = new HashSet<uint> { 1u, 2u, 2u };
        sb.AppendLine(hs.Count.ToString());

        var arr = new uint[] { 5u, 6u, 7u };
        sb.AppendLine(Array.IndexOf(arr, 6u).ToString());
        sb.AppendLine(arr.Aggregate((x, y) => x + y).ToString());

        var span = new Span<uint>(arr);
        sb.AppendLine(span[0].ToString());

        Span<uint> stackSpan = stackalloc uint[3];
        stackSpan[0] = 9u;
        stackSpan[1] = 8u;
        stackSpan[2] = 7u;
        sb.AppendLine(stackSpan[0].ToString());
        return sb.ToString();
    }
}
