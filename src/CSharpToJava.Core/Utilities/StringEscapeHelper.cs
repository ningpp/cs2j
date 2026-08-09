using System.Text;

namespace CSharpToJava.Core.Utilities;

internal static class StringEscapeHelper
{
    internal static string EscapeJavaString(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\"': sb.Append("\\\""); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\0': sb.Append("\\u0000"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else if (char.IsSurrogate(c))
                        // Surrogate code units (e.g. RegexCharClass category ranges
                        // like "\uD800\uDB80"): the .NET UTF-8 encoder replaces unpaired
                        // surrogates with U+FFFD when writing the output file. Emit the
                        // Java \u escape so the exact code unit survives.
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
