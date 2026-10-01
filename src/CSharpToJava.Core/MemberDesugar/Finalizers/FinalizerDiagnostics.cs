namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// CS2J5200-5211 消息文案（设计 §6，风格对齐 Events/EventDiagnostics）。消息含
/// [Ns.Type]、体分类、配对结论与迁移动作建议。
/// </summary>
internal static class FinalizerDiagnostics
{
    /// <summary>CS2J5200：A 档显式拒绝，成员保留。</summary>
    public static string Rejected(string typeFqn, FinalizerBodyKind kind, SuppressFinalizePairing pairing)
        => $"finalizer on [{typeFqn}] is retained (policy Reject): C# finalizers run non-deterministically "
           + $"and have no Java equivalent (java.lang.Object.finalize() is deprecated); body classified as "
           + $"{KindLabel(kind)}, GC.SuppressFinalize pairing: {PairingLabel(pairing)}. Migrate the cleanup to "
           + "a deterministic Dispose/using path, or explicitly opt into removal via rules.finalizers "
           + "(Policy=Remove with per-type ForceRemoveTypes).";

    /// <summary>CS2J5205：CriticalFinalizerObject 派生，任何策略下不可自动删除。</summary>
    public static string CriticalBlocked(string typeFqn)
        => $"finalizer on [{typeFqn}] cannot be removed or guided: the type derives from "
           + "System.Runtime.ConstrainedExecution.CriticalFinalizerObject (CER contract); the finalizer "
           + "member is retained. Review the platform contract manually before any Java conversion.";

    /// <summary>CS2J5208：CleanerGuide v1 未实现。</summary>
    public static string CleanerGuideUnsupported(string typeFqn)
        => $"rules.finalizers Policy=CleanerGuide is not implemented for [{typeFqn}] in this version "
           + "(design §2-8); use Reject (default, explicit diagnostic) or Remove (explicit escape hatch).";

    /// <summary>CS2J5211：单文件模式分析降级。</summary>
    public static string SingleFileDegraded(string typeFqn)
        => $"single-file mode: base-chain analysis and whole-compilation body classification for the "
           + $"finalizer on [{typeFqn}] are degraded; SafeHandle/native-release classifications are "
           + "conservatively reported as Other (CS2J5211).";

    /// <summary>CS2J5202：标准 Dispose 模式 + 配对（Info）。</summary>
    public static string DisposePattern(string typeFqn)
        => $"finalizer on [{typeFqn}] follows the standard dispose pattern (Dispose(false) paired with "
           + "GC.SuppressFinalize(this)); this is the lowest-risk candidate for manual removal after review.";

    /// <summary>CS2J5203：冗余终结器（空体/仅 SafeHandle 字段操作）（Info）。</summary>
    public static string Redundant(string typeFqn, FinalizerBodyKind kind)
        => $"finalizer on [{typeFqn}] is redundant ({KindLabel(kind).ToLowerInvariant()}): an empty body adds "
           + "no cleanup, and SafeHandle/CriticalHandle already run a critical finalizer; a user finalizer "
           + "only delays release.";

    /// <summary>CS2J5206：GC 生命周期 API 站点清点（Info）。</summary>
    public static string GcLifecycleSites(
        string typeFqn, int suppress, int reRegister, int waitForPending)
        => $"GC lifecycle call sites in [{typeFqn}]: GC.SuppressFinalize x{suppress}, "
           + $"GC.ReRegisterForFinalize x{reRegister} (resurrection — high risk), "
           + $"GC.WaitForPendingFinalizers x{waitForPending}; none of these calls are rewritten.";

    /// <summary>CS2J5207：基类终结器链信息（Info）。</summary>
    public static string BaseChain(string typeFqn, string? baseFqn, bool degraded)
    {
        var who = baseFqn != null ? $"base type [{baseFqn}] also declares a finalizer" : "base-chain details unavailable";
        var note = degraded ? " Analysis is degraded (single-file mode or missing cross-assembly metadata)." : null;
        return $"finalizer chain for [{typeFqn}]: {who}; after source-level removal the base finalizer still "
             + "runs independently at runtime (compiler-inserted base.Finalize() disappears with the override)."
             + note;
    }

    public static string KindLabel(FinalizerBodyKind kind) => kind switch
    {
        FinalizerBodyKind.Empty => "Empty",
        FinalizerBodyKind.DisposePattern => "DisposePattern",
        FinalizerBodyKind.SafeHandleOnly => "SafeHandleOnly",
        FinalizerBodyKind.NativeRelease => "NativeRelease",
        FinalizerBodyKind.Extern => "Extern",
        _ => "Other",
    };

    public static string PairingLabel(SuppressFinalizePairing pairing) => pairing switch
    {
        SuppressFinalizePairing.Paired => "paired (Dispose + GC.SuppressFinalize(this))",
        SuppressFinalizePairing.Unpaired => "unpaired (no deterministic entry suppresses this finalizer)",
        _ => "none",
    };
}
