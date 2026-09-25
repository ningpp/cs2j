using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ArrayMethodRewriter;

/// <summary>
/// 标记被 array-method 预处理重写的节点。外层标记节点决定最终只做局部格式化的
/// TextSpan 范围，避免整文件重排带来的大 diff。
/// </summary>
internal static class ArrayRewriteAnnotations
{
    public const string Kind = "Cs2jArrayRewrite";

    public static SyntaxAnnotation Rewritten { get; } = new(Kind);

    public static TSyntax Mark<TSyntax>(TSyntax node) where TSyntax : SyntaxNode
        => node.WithAdditionalAnnotations(Rewritten);
}
