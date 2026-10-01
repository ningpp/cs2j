namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>C39 终结器处置策略（设计 §2 决策 1/8/9）。M1 仅 Reject/CleanerGuide 发诊断；
/// Remove 的实际删除为 M2，M1 配置该值时行为与 Reject 一致（成员保留 + CS2J5200）。</summary>
public enum FinalizerPolicy
{
    /// <summary>A 档（推荐默认）：显式报码、成员保留、不重写。</summary>
    Reject,

    /// <summary>B 档（M2 交付）：确认后删除 + sidecar 留痕；M1 不生效，按 Reject 处理。</summary>
    Remove,

    /// <summary>C 档（远期）：Cleaner 引导；v1 未实现，配置即 CS2J5208 Error。</summary>
    CleanerGuide,
}

/// <summary>终结器体六分类（FN-2，设计 §2 决策 4）。</summary>
public enum FinalizerBodyKind
{
    /// <summary>空体/仅白名单噪声（Debug.Assert、注释）。</summary>
    Empty,

    /// <summary>唯一有效语句为 Dispose(false)/Dispose() 形态。</summary>
    DisposePattern,

    /// <summary>仅触碰 System.Runtime.InteropServices.SafeHandle/CriticalHandle 派生字段。</summary>
    SafeHandleOnly,

    /// <summary>含 Marshal.Free*/NativeLibrary.Free 等非托管释放调用。</summary>
    NativeRelease,

    /// <summary>上述之外（flush、日志、自定义逻辑）；单文件降级时 SafeHandle/Native 保守归入此项。</summary>
    Other,

    /// <summary>无体（extern 终结器 <c>extern ~T();</c>）。</summary>
    Extern,
}

/// <summary>GC.SuppressFinalize 配对结论（FN-3，设计 §2 决策 5）。</summary>
public enum SuppressFinalizePairing
{
    /// <summary>同类型声明 Dispose（0/1 参）且存在 GC.SuppressFinalize(this) 站点。</summary>
    Paired,

    /// <summary>终结器存在但不满足配对（无 Dispose 或无 SuppressFinalize 站点）。</summary>
    Unpaired,

    /// <summary>保留值：M1 的 plan 只会落 Paired/Unpaired（无终结器类型不产 plan）。</summary>
    None,
}

/// <summary>rules.finalizers 子节选项（设计 §2 决策 9）。族级 Strict 不在此重复。
/// M1 只承载：Policy 生效（Reject 报码 / CleanerGuide 报错）；ForceRemoveTypes 与两个
/// 可选项为 M2 删除模式预留，本阶段不产生任何行为差异。</summary>
public sealed class FinalizerOptions
{
    /// <summary>处置策略，默认 Reject（A 档显式拒绝）。</summary>
    public FinalizerPolicy Policy { get; init; } = FinalizerPolicy.Reject;

    /// <summary>逐类型 FQN 白名单（M2 仅用于解除 CS2J5204，永不解除 CS2J5205）。M1 承载不生效。</summary>
    public IReadOnlySet<string> ForceRemoveTypes { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>M2：删除时是否原位留 marker 注释（默认关；M1 不生效）。</summary>
    public bool EmitRemovalMarkerComment { get; init; }

    /// <summary>M2：删除时是否把 body 提取为永不调用的私有方法（默认关；M1 不生效）。</summary>
    public bool ExtractRemovedBody { get; init; }
}
