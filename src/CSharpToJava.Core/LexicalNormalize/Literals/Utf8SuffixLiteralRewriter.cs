using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// LL-2: "x"u8 → byte[] array carrying the compile-time UTF-8 blob (design
/// D4/D5). Byte computation uses Encoding.UTF8 (Core-side only); the M0 probe
/// (Task 1) locks the lone-surrogate behavior. C# 11 implicit span conversion
/// covers ReadOnlySpan&lt;byte&gt; use sites; verify build gates them.
/// </summary>
internal static class Utf8SuffixLiteralRewriter
{
    public static ArrayCreationExpressionSyntax? TryRewrite(
        LiteralExpressionSyntax node,
        LiteralRewriteRecorder recorder)
    {
        if (LiteralSyntaxFacts.Classify(node) != LiteralKind.Utf8)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(node.Token.ValueText);
        var replacement = ByteLiteralFactory.Create(bytes).WithTriviaFrom(node);
        recorder.RecordUtf8(bytes);
        return replacement;
    }
}
