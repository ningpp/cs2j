// src/CSharpToJava.Core/Java2/CodeGen/CommentWriter.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public static class CommentWriter
{
    public static void WriteLeading(IndentedWriter w, string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return;
        foreach (var line in comment.Split('\n'))
            w.WriteLine("// " + line.TrimEnd('\r').TrimStart());
    }
}
