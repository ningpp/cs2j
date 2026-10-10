using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ArrayMethodRewriter;

/// <summary>
/// 标记被 array-method 预处理重写的节点。外层标记节点决定最终只做局部格式化的
/// TextSpan 范围，避免整文件重排带来的大 diff。
/// </summary>
internal static class ArrayRewriteAnnotations
{
    public const string Kind = "Cs2jArrayRewrite";

    /// <summary>
    /// 多维 foreach 摊平合成的行变量前缀（<c>__cs2j_row0</c> …）。中间树经
    /// 文本往返后语法注解丢失，此前缀是转换器识别摊平形态的唯一持久信号。
    /// </summary>
    public const string RowVariablePrefix = "__cs2j_row";

    public static SyntaxAnnotation Rewritten { get; } = new(Kind);

    public static TSyntaxNode Mark<TSyntaxNode>(TSyntaxNode node) where TSyntaxNode : SyntaxNode
        => node.WithAdditionalAnnotations(Rewritten);

    /// <summary>True for synthesized row-variable names emitted by the foreach flatten.</summary>
    public static bool IsRowVariableName(string text)
    {
        if (!text.StartsWith(RowVariablePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = text.AsSpan(RowVariablePrefix.Length);
        if (suffix.Length == 0)
        {
            return false;
        }

        foreach (var c in suffix)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
