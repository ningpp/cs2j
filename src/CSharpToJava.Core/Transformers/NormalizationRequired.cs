using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// normalization-required fail-fast 异常的统一构造点：在消息尾部追加表达式级
/// 源定位（文件(行,列) + 截断的源文本）。外层 TypeGroupResolver 的 Source 只能
/// 定位到类型声明行，无法指出类内哪个表达式触发；没有这层定位，每次 fail-fast
/// 都要在类体里人工搜形态。
/// </summary>
internal static class NormalizationRequired
{
    public static NotSupportedException At(string construct, SyntaxNode node) =>
        new($"CS2J normalization required: {construct} must be lowered by a preprocessor before Java conversion.{FormatSpan(node)}");

    public static NotSupportedException At(string construct, SyntaxNode node, SyntaxNode location) =>
        new($"CS2J normalization required: {construct} must be lowered by a preprocessor before Java conversion.{FormatSpan(location)}");

    internal static string FormatSpan(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        var text = node.ToString().Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 80) text = text[..80] + "…";
        return $" [at {span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {text}]";
    }
}
