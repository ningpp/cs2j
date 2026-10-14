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

    /// <summary>
    /// 偏移是否折叠到「同名/形参索引」变量自身（指针形参的配对 int 索引形参、
    /// holder.Value copy-in 的 int shadow 局部、配对指针形参别名出的 int
    /// cursor 局部）。为 true 时自增/复合赋值直接作用在 <see cref="OffsetText"/>
    /// 指向的既有变量上，不再插入合成索引局部，也不做 lambda 捕获阻塞。
    /// </summary>
    public bool FoldsToOffset { get; init; }

    /// <summary>
    /// 真源源槽键：两个可证游标仅当源槽相等才允许指针比较/相减归一。
    /// null 时回退 <see cref="ArrayExpressionText"/>（数组文本即槽）。
    /// 独立指针形参各自成槽；末端形参（pEnd）归属基指针槽；R3 fixed
    /// 游标按真源（fixed 初始式溯源的局部/形参/字段符号）归槽。
    /// </summary>
    public string? SourceSlot { get; init; }
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

    /// <summary>全部已登记 flow（R3 按伴生数组文本反查源槽归属）。</summary>
    public IEnumerable<PointerFlow> EnumerateFlows() => _flows.Values;
}
