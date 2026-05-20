namespace CSharpToJava.Core.Utilities;

internal static class StringEscapeHelper
{
    internal static string EscapeJavaString(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\t", "\\t")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\a", "\\u0007")
            .Replace("\v", "\\u000B");
    }
}
