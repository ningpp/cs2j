using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// 一个已冻结的 await foreach 展开计划（设计 §2.1/§4.2、D2/D3/D6/D9）。
/// 链（WithCancellation/ConfigureAwait）已在 Analyzer 中由内而外消解：
/// <see cref="ReceiverExpression"/> 是消解后的真实枚举源（只求值一次），
/// <see cref="TokenExpression"/> 是进 GetAsyncEnumerator 的实参（裸源＝CancellationToken.None）。
/// </summary>
/// <param name="Node">原始 await foreach 节点（恒为普通声明形态；解构形态在分析期已 1840 拒绝）。</param>
/// <param name="ReceiverExpression">消解后源表达式（WithCancellation/ConfigureAwait 已剥除）。</param>
/// <param name="TokenExpression">GetAsyncEnumerator 实参（CT 实参或全局 CancellationToken.None）。</param>
/// <param name="EnumeratorName">枚举器临时量名 __cs2jAsE_n（成员作用域防撞后）。</param>
/// <param name="OkName">保真守卫成功标志位名 __cs2jAsOk_n（朴素形态不发射）。</param>
/// <param name="ElementType">元素静态类型符号（显式类型渲染用；var 形态不消费）。</param>
/// <param name="ElementTypeIsVar">源 foreach 是否 var 元素（var 保持 var）。</param>
/// <param name="HasWithCancellation">本站点是否消解了一个 WithCancellation 链（统计桶）。</param>
/// <param name="ConfigureAwaitStripped">本站点剥除的枚举器 ConfigureAwait 链数（CS2J1830/统计桶）。</param>
internal sealed record AsyncForeachSite(
    ForEachStatementSyntax Node,
    ExpressionSyntax ReceiverExpression,
    ExpressionSyntax TokenExpression,
    string EnumeratorName,
    string OkName,
    ITypeSymbol? ElementType,
    bool ElementTypeIsVar,
    bool HasWithCancellation,
    int ConfigureAwaitStripped);
