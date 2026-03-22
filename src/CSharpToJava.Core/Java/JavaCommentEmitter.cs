using System.Text;

namespace CSharpToJava.Core.Java;

internal static class JavaCommentEmitter
{
    public static void AppendLeadingComment(StringBuilder sb, string indentation, string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return;

        foreach (var line in comment.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            sb.Append(indentation).AppendLine(line.TrimEnd());
        }
    }
}