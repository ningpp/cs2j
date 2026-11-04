using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// 归约残留在链末审计（normalization-audit）与 Java 转换门禁之间共享的处置策略。
/// </summary>
public enum ResidualPolicy
{
    /// <summary>Java 转换器会以 "CS2J normalization required" 硬失败，必须由 owner pass 消除。</summary>
    Fail,

    /// <summary>允许保留，但链末审计必须以 warning 形式报告。</summary>
    PreserveAndWarn,

    /// <summary>已登记为显式不支持域，需要带文件、行号与原因。</summary>
    DeclaredUnsupported,
}

/// <summary>
/// 一个 NORM 构造的稳定契约。Code 是稳定 ID，JavaGate 描述 Java 转换器的对应门禁。
/// </summary>
public sealed record ResidualDescriptor(
    string Code,
    string OwnerPassId,
    ResidualPolicy Policy,
    string JavaGate);

/// <summary>单次扫描命中的残留。Line/Column 均为 1-based，Snippet 已单行化并截断。</summary>
public sealed record ResidualHit(
    string Code,
    string OwnerPassId,
    ResidualPolicy Policy,
    string FilePath,
    int Line,
    int Column,
    string Snippet,
    string Reason);

/// <summary>只读残留探针：只检查语法/语义，不做任何改写。</summary>
public interface IResidualProbe
{
    string Code { get; }

    IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath);
}

/// <summary>探针共用的 hit 构造与片段格式化。</summary>
internal static class ResidualHitFactory
{
    internal const int MaxSnippetLength = 120;

    internal static ResidualHit Create(
        string code,
        string ownerPassId,
        ResidualPolicy policy,
        string relativePath,
        SyntaxNode node,
        string reason)
    {
        var start = node.GetLocation().GetLineSpan().StartLinePosition;
        return new ResidualHit(
            code,
            ownerPassId,
            policy,
            relativePath,
            start.Line + 1,
            start.Character + 1,
            SingleLineSnippet(node.ToString()),
            reason);
    }

    private static string SingleLineSnippet(string text)
    {
        var singleLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length > MaxSnippetLength
            ? singleLine[..MaxSnippetLength]
            : singleLine;
    }
}
