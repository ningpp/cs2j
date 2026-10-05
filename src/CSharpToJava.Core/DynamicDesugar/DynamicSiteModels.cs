namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>dynamic 站点分类（设计 §4.2 表；诊断码与报告 JSON 共用同一词汇，共 13 个 kind）。</summary>
public enum DynamicSiteKind
{
    // ---- 声明族 ----

    /// <summary>局部声明显式 dynamic，或 var 推断为 dynamic（含 foreach/catch/解构 dynamic 分量）。</summary>
    DynamicLocalDecl,

    /// <summary>方法/索引器/委托/lambda/本地函数的 dynamic 形参（DS011）。</summary>
    DynamicParameter,

    /// <summary>方法/委托/本地函数 dynamic 返回类型（DS011）。</summary>
    DynamicReturn,

    /// <summary>字段/属性/事件/索引器的 dynamic 类型（DS012，永不自动重写）。</summary>
    DynamicFieldOrProperty,

    // ---- 操作族 ----

    /// <summary>动态调用（无静态 Symbol / LateBound 候选）（DS013）。</summary>
    DynamicInvocation,

    /// <summary>动态成员访问（含方法组绑定、事件订阅）（DS013）。</summary>
    DynamicMemberAccess,

    /// <summary>dynamic 索引（DS014）。</summary>
    DynamicElementAccess,

    /// <summary>二元/一元/复合赋值/自增自减/dynamic await，任一运算元动态即整式动态（DS015）。</summary>
    DynamicOperation,

    /// <summary>向 dynamic 的隐式转换（动态实参/赋值右值，记录来源 T）（DS016）；反向 (T)d 不记本 kind。</summary>
    DynamicConversion,

    // ---- 边界 API 族 ----

    /// <summary>System.Dynamic.ExpandoObject / System.Dynamic.DynamicObject（含子类与构造）（DS017）。</summary>
    ExpandoOrDynamicObject,

    /// <summary>JObject/JToken/DeserializeObject&lt;dynamic&gt; 等动态 JSON 用法（DS017 子因）。</summary>
    JsonDynamic,

    /// <summary>Microsoft.CSharp.RuntimeBinder.*（RuntimeBinderException/CallSite/Binder 显式引用）（DS018）。</summary>
    RuntimeBinderApi,

    // ---- 元数据族 ----

    /// <summary>外部程序集 object+[Dynamic] 形参/字段/返回在源内的使用点，或源内手写 [Dynamic] object 声明（DS019）。</summary>
    DynamicAttributeEntry,
}

/// <summary>站点可证明性（设计 D2）。M1 无证明器，dynamic 局部一律 <see cref="Unclassified"/>。</summary>
public enum DynamicProvability
{
    /// <summary>未分类（M1 审计；P1/P2/不可证判定在 M2/保守扫描侧给出）。</summary>
    Unclassified,

    /// <summary>P1：单具体 sealed 类型、无逃逸，M2 可具体化（M2 证明器产出）。</summary>
    ProvenP1,

    /// <summary>P2：用户已写 (T)d 显式转换，本就静态绑定，仅 Info 提示。</summary>
    P2Info,

    /// <summary>不可证（rewrite 模式 Error；M2 证明器产出具体原因）。</summary>
    Unprovable,
}

/// <summary>不可证原因（设计 §4.2 九值；M1 仅保守扫描会用 <see cref="UnresolvedSymbol"/>）。</summary>
public enum DynamicUnprovableReason
{
    /// <summary>来源类型非 sealed（开放类型；P1 唯一系统性语义差是成员隐藏）。</summary>
    SealedNotSatisfied,

    /// <summary>变量存在两个及以上不同静态类型赋值点（宁假阳，不合并共同基类）。</summary>
    MultipleAssignmentTypes,

    /// <summary>某赋值右值本身为 dynamic/不可解析。</summary>
    DynamicAssignment,

    /// <summary>变量作为 ref/out/in 实参逃逸。</summary>
    ByRefEscape,

    /// <summary>变量被匿名函数/本地函数捕获，或处于 fixed/lock/using/await 等特殊位置。</summary>
    Captured,

    /// <summary>参数/返回等跨边界签名（M2 不改签名）。</summary>
    CrossBoundarySignature,

    /// <summary>字段/属性/事件/索引器存储（永不自动重写）。</summary>
    FieldLikeStorage,

    /// <summary>仅能从元数据观察、源内不可追溯的 dynamic 面（如前置 pass 物化的 object 存储）。</summary>
    MetadataOnly,

    /// <summary>符号不可解析/基线 error 节点（R10）；或 R11 边界 API 引用缺失时的保守识别。</summary>
    UnresolvedSymbol,
}

/// <summary>
/// 统一 dynamic 站点记录（设计 §4.2）。
/// 行列在本记录内以 0 基存储（Roslyn LinePosition 原值）；报告 JSON 与诊断输出统一 +1。
/// 排序键为 (FilePath, SpanStart, SpanEnd, Kind, Code)，保证跨运行字节稳定。
/// </summary>
public sealed class DynamicSiteRecord
{
    /// <summary>站点分类。</summary>
    public required DynamicSiteKind Kind { get; init; }

    /// <summary>语法树文件路径（tree.FilePath，可能为空串）。</summary>
    public required string FilePath { get; init; }

    /// <summary>节点 span 起点（字符偏移）。</summary>
    public required int SpanStart { get; init; }

    /// <summary>节点 span 终点（字符偏移，不含）。</summary>
    public required int SpanEnd { get; init; }

    /// <summary>起始行（0 基；输出时 +1）。</summary>
    public required int Line { get; init; }

    /// <summary>起始列（0 基；输出时 +1）。</summary>
    public required int Column { get; init; }

    /// <summary>语法节点种类名（SyntaxNode.Kind()）。</summary>
    public required string SyntaxKindName { get; init; }

    /// <summary>表达式/签名摘要（节点文本，空白归一化并截断；仅报告与诊断用）。</summary>
    public string ExpressionText { get; init; } = string.Empty;

    /// <summary>接收者表达式文本（能取到时）。</summary>
    public string? ReceiverText { get; init; }

    /// <summary>绑定符号显示名（能取到时；晚绑定站点通常为 null）。</summary>
    public string? BoundSymbolName { get; init; }

    /// <summary>dynamic 边界处的编译期来源类型 T 显示名（DS016 用；不能取到时为 null）。</summary>
    public string? OriginType { get; init; }

    /// <summary>可证明性。M1 dynamic 局部恒 <see cref="DynamicProvability.Unclassified"/>。</summary>
    public DynamicProvability Provability { get; init; } = DynamicProvability.Unclassified;

    /// <summary>不可证原因（M1 通常为 null；基线 error 保守记录为 UnresolvedSymbol）。</summary>
    public DynamicUnprovableReason? UnprovableReason { get; init; }

    /// <summary>站点诊断码（DS0xx）。</summary>
    public required string Code { get; init; }

    /// <summary>诊断/报告消息（具名、可操作，设计 §6.1）。</summary>
    public required string Message { get; init; }

    /// <summary>是否命中审计 manifest（M2；M1 恒 false 占位）。</summary>
    public bool ManifestHit { get; init; }

    /// <summary>
    /// 引用缺失、按限定名文本保守识别（R11）：消息带「未解析引用，请人工确认」。
    /// 与 <see cref="DynamicUnprovableReason.UnresolvedSymbol"/> 的噪声抑制相互独立——
    /// 边界 API 保守识别宁假阳，仍发射 Warning。
    /// </summary>
    public bool SymbolUnresolved { get; init; }
}
