using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams.AsyncIterator;

/// <summary>
/// 一个提升进联合状态机枚举器类的形参（设计 D7）。字段名确定性、全 ASCII。
/// </summary>
/// <param name="Symbol">形参符号。</param>
/// <param name="FieldName">枚举器/包装类内字段名。</param>
/// <param name="HasEnumeratorCancellation">是否带 [EnumeratorCancellation]（CT 合并依据）。</param>
internal sealed record AsyncIteratorParameter(IParameterSymbol Symbol, string FieldName,
    bool HasEnumeratorCancellation);

/// <summary>
/// 单个 async 迭代器方法的降级计划（工厂方法 + 枚举器类 [+ 可枚举包装类]）。
/// 由 <see cref="AsyncIteratorPlanner"/> 在「post-C18 → await foreach/using 展开后」
/// 的重绑模型上冻结；活性提升/区域表由 <see cref="AsyncIteratorLowering"/> 消费。
/// </summary>
/// <param name="Node">原方法声明节点（重写时替换为非 async 工厂）。</param>
/// <param name="Symbol">方法符号。</param>
/// <param name="Index">全文件源序序号 n（命名后缀）。</param>
/// <param name="EnumeratorClassName">枚举器类名 Cs2jAsyncIterator_&lt;宿主&gt;_&lt;n&gt;。</param>
/// <param name="EnumerableClassName">IAsyncEnumerable 形态的包装类名；IAsyncEnumerator 形态为 null。</param>
/// <param name="ElementType">流元素类型 T。</param>
/// <param name="IsEnumerable">true=IAsyncEnumerable&lt;T&gt;（出包装类）；false=IAsyncEnumerator&lt;T&gt;。</param>
/// <param name="ContainingType">宿主类型符号（__cs2jThis 字段类型/静态成员限定）。</param>
/// <param name="Parameters">按源序提升的全部形参。</param>
/// <param name="CancellationTokenParameter">承担 GetAsyncEnumerator 令牌合并的 CT 形参（[EnumeratorCancellation]）；无则 null。</param>
/// <param name="YieldReturnCount">yield return 个数（=最大 state 值）。</param>
internal sealed record AsyncIteratorPlan(
    MethodDeclarationSyntax Node,
    IMethodSymbol Symbol,
    int Index,
    string EnumeratorClassName,
    string? EnumerableClassName,
    ITypeSymbol ElementType,
    bool IsEnumerable,
    INamedTypeSymbol ContainingType,
    IReadOnlyList<AsyncIteratorParameter> Parameters,
    IParameterSymbol? CancellationTokenParameter,
    int YieldReturnCount);

/// <summary>
/// 单文件 AS-6 迭代器分析结果：可降级计划 + 冻结成员（拒绝/基线错误，整成员 skip）
/// + 诊断。冻结成员体内 await foreach/await using 站点不计划、不部分改写。
/// </summary>
internal sealed record AsyncIteratorAnalysis(
    IReadOnlyList<AsyncIteratorPlan> Plans,
    IReadOnlyList<SyntaxNode> FrozenNodes,
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics);
