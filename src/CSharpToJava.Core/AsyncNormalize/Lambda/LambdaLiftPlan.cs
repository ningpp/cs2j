using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.Lambda;

/// <summary>D3 捕获四型中的可提升二分（void wrapper 由 <see cref="LambdaLiftPlan.NeedsWrapper"/> 另表）。</summary>
internal enum AsyncLambdaCaptureKind
{
    /// <summary>零捕获：提为 <c>private static async</c>（D3(a)）。</summary>
    ZeroCapture,

    /// <summary>仅捕获 this：提为 <c>private async</c> 实例方法（D3(b)）。</summary>
    ThisOnly,

    /// <summary>捕获局部/形参/foreach 迭代变量：v1 CS2J1802 defer（D3(d)）。</summary>
    CapturedLocals,
}

/// <summary>提升方法一个形参的冻结渲染信息（名取 lambda 语法原名，型取委托 Invoke 符号）。</summary>
internal sealed record LambdaParameterPlan(
    string Name,
    string TypeDisplay,
    RefKind RefKind);

/// <summary>
/// 捕获布局（设计 §4.4 D12）：被捕获 <see cref="ISymbol"/> → 合成字段名只读表。
/// v1 只有零捕获/this 两型可提升，无字段发射（this 直接落实例方法），表结构先行冻结，
/// 等 C45 closure-lift 落地后由 display class 消费。
/// </summary>
internal sealed class CaptureLayout
{
    public static readonly CaptureLayout Empty = new(
        new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default));

    private readonly IReadOnlyDictionary<ISymbol, string> _fields;

    public CaptureLayout(IReadOnlyDictionary<ISymbol, string> fields)
    {
        _fields = fields;
    }

    public IReadOnlyDictionary<ISymbol, string> Fields => _fields;
}

/// <summary>
/// 一个 async lambda / 残余 async 局部函数的不可变提升计划（设计 §4.1/§4.2）。
/// Analyzer 在任何改写前冻结；Lifter 只消费计划，不再做判定。
/// </summary>
internal sealed record LambdaLiftPlan(
    /// <summary>源节点：ParenthesizedLambdaExpression / SimpleLambdaExpression / LocalFunctionStatement。</summary>
    SyntaxNode Node,

    /// <summary>true = 残余 async 局部函数（D4）；false = async lambda（D3）。</summary>
    bool IsLocalFunction,

    /// <summary>归属类型声明（提升方法/wrapper 的插入点）。</summary>
    TypeDeclarationSyntax HostType,

    /// <summary>宿主成员名（命名契约 Cs2jAsync_&lt;宿主成员名&gt;_&lt;n&gt;）。</summary>
    string HostMemberName,

    /// <summary>类型作用域内同宿主成员名的确定性序号。</summary>
    int Index,

    /// <summary>消解碰撞后的提升方法名。</summary>
    string LiftedMethodName,

    /// <summary>D3(c) 同步丢弃 wrapper 名；非 void 委托为 null。</summary>
    string? WrapperName,

    /// <summary>true = private static（零捕获）；false = private 实例（仅 this）。</summary>
    bool IsStatic,

    /// <summary>目标委托返回 void（async void lambda）：方法提为 async Task + 合成 _w wrapper。</summary>
    bool NeedsWrapper,

    /// <summary>目标委托类型符号（局部函数计划为 null）。</summary>
    INamedTypeSymbol? DelegateType,

    /// <summary>委托类型 global:: 全限定渲染（调用点 new 形态）；局部函数计划为 null。</summary>
    string? DelegateTypeDisplay,

    /// <summary>提升方法形参（名来自源码，类型来自符号）。</summary>
    IReadOnlyList<LambdaParameterPlan> Parameters,

    /// <summary>提升方法返回类型渲染：void 委托为 Task，其余为委托 Invoke 返回类型（Task/Task&lt;T&gt;）。</summary>
    string LiftedReturnTypeDisplay,

    /// <summary>
    /// 表达式体源节点转语句时是否需要 return：Task/void 形态为 false（表达式语句），
    /// Task&lt;T&gt;/ValueTask&lt;T&gt; 形态为 true（return 表达式）。
    /// </summary>
    bool ExpressionBodyNeedsReturn,

    /// <summary>捕获分类。</summary>
    AsyncLambdaCaptureKind CaptureKind,

    /// <summary>提升方法需携带的泛型参数名（外层方法类型参数前置、去重、仅保留实际使用的）。</summary>
    IReadOnlyList<string> MethodTypeParameters,

    /// <summary>与 <see cref="MethodTypeParameters"/> 配套的 where 约束子句（已按参数过滤）。</summary>
    IReadOnlyList<TypeParameterConstraintClauseSyntax> ConstraintClauses,

    /// <summary>v1 恒为 <see cref="CaptureLayout.Empty"/>。</summary>
    CaptureLayout CaptureLayout)
{
    /// <summary>调用点放入委托构造的方法名（wrapper 优先）。</summary>
    public string CallSiteMethodName => WrapperName ?? LiftedMethodName;
}
