using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// 指针符号的指向源分类（设计 D1/D7，§4 UnsafeFfmAnalyzer①）。
/// </summary>
internal enum PointerSourceKind
{
    /// <summary>指向托管数组元素（表达式 <c>&amp;arr[i]</c>，偏移 = i）。</summary>
    ArrayElement,

    /// <summary>指向数组字段（R2 fixed 字段缓冲区改写后的数组字段）。</summary>
    ArrayField,

    /// <summary>来自被 R5 改写的指针形参（参数即数组）。</summary>
    ParamArray,

    /// <summary>标量槽（<c>&amp;scalar</c> 单元素槽；M3a 不做成功归一）。</summary>
    ScalarSlot,

    /// <summary>来源不可证。</summary>
    Unknown,
}

/// <summary>
/// 一个指针符号的流向事实：指向源 + 数组表达式文本 + 元素偏移文本。
/// 数组表达式以<b>文本</b>保存：R5 建模型后树还会被 R3/R4 重建，保存旧
/// 节点会脱离树；R6 消费时按文本在自己的目标树上重新解析（trivia 由
/// 最终格式化统一）。M3a 全部成功路径偏移均为 0；非零偏移（&amp;arr[i]
/// 与算术传播）由 R3 域或 M3b 消费。
/// </summary>
internal sealed record PointerFlow(
    PointerSourceKind Kind,
    string ArrayExpressionText,
    string OffsetText = "0")
{
    /// <summary>来源是否可证（R6 可索引化）。</summary>
    public bool IsProvable => Kind != PointerSourceKind.Unknown;
}

/// <summary>
/// 指针流向模型：每个指针符号（原始语义模型符号）→ <see cref="PointerFlow"/>。
/// R5 <see cref="PointerSignatureRewriter"/> 构建，R6
/// <see cref="PointerExprRewriter"/> 消费；同一对象在内存链中传递。
/// </summary>
internal sealed class PointerFlowModel
{
    private readonly Dictionary<ISymbol, PointerFlow> _flows =
        new(SymbolEqualityComparer.Default);

    public void Register(ISymbol symbol, PointerFlow flow) => _flows[symbol] = flow;

    public bool TryGet(ISymbol? symbol, out PointerFlow flow)
    {
        if (symbol != null && _flows.TryGetValue(symbol, out var found))
        {
            flow = found;
            return true;
        }

        flow = default!;
        return false;
    }

    public bool Contains(ISymbol? symbol) =>
        symbol != null && _flows.ContainsKey(symbol);
}
