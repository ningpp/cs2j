namespace CSharpToJava.Core.ConcurrencyNormalize;

public enum ConcurrencySeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CN-x 规则编号（设计 §4.3）；显示形为 CN-1..CN-12。</summary>
public enum ConcurrencyRule
{
    /// <summary>CN-1 lock 保留直映，仅计数与 CN-6 审计。</summary>
    CN1,

    /// <summary>CN-2 手写 Enter/Exit 配对逆向归一为 lock。</summary>
    CN2,

    /// <summary>CN-3 TryEnter 规范形（3 参 int 毫秒 + lockTaken + finally 守卫）。</summary>
    CN3,

    /// <summary>CN-4 Wait 重载归一。</summary>
    CN4,

    /// <summary>CN-5 Pulse/PulseAll 持锁校验与计数。</summary>
    CN5,

    /// <summary>CN-6 锁对象身份审计。</summary>
    CN6,

    /// <summary>CN-7 volatile 字段审计（不重写）。</summary>
    CN7,

    /// <summary>CN-8 Interlocked/Thread 静态 API 不动。</summary>
    CN8,

    /// <summary>CN-9 System.Threading.Volatile 静态 API 计数。</summary>
    CN9,

    /// <summary>CN-10 lock 区域内 await 的防御镜像。</summary>
    CN10,

    /// <summary>CN-11 范围外同步原语盘点。</summary>
    CN11,

    /// <summary>CN-12 递归/异常释放语义（fixture 守门）。</summary>
    CN12,
}

internal static class ConcurrencyRuleDisplay
{
    public static string ToDisplay(this ConcurrencyRule rule) => $"CN-{(int)rule + 1}";
}

/// <summary>诊断码常量表（设计 §6）。码位只追加不改号。</summary>
public static class ConcurrencyDiagnosticCodes
{
    /// <summary>整树重编译新增错误且无法归属单一 region plan → 全部回退。</summary>
    public const string WholeTreeRollback = "CS2J2900";

    /// <summary>新增错误归属某方法 plan → 该 plan 原子回退。</summary>
    public const string PlanRollback = "CS2J2901";

    /// <summary>中间树 restore 失败。</summary>
    public const string RestoreFailed = "CS2J2902";

    /// <summary>中间树 build 失败。</summary>
    public const string BuildFailed = "CS2J2903";

    /// <summary>中间树 test 失败。</summary>
    public const string TestFailed = "CS2J2904";

    /// <summary>单文件模式 plan 非自包含（保留位，稳态不触发）。</summary>
    public const string SingleFilePlanNotSelfContained = "CS2J2909";

    /// <summary>手写 Enter/Exit 或 TryEnter 结构非规范，区域原样不动（strict Error）。</summary>
    public const string NonCanonicalStructure = "CS2J2910";

    /// <summary>TryEnter 已规范为显式形，Java 半区无 intrinsic tryLock，语义挂账（strict Error）。</summary>
    public const string TryEnterGate = "CS2J2911";

    /// <summary>Wait/Pulse/PulseAll 不能词法-保守证明持锁（strict Error）。</summary>
    public const string LockOwnershipUnproven = "CS2J2920";

    /// <summary>Wait(exitContext: true) 无 Java 对应，站点原样（strict Error）。</summary>
    public const string WaitExitContext = "CS2J2921";

    /// <summary>lock 区域内 await 的防御镜像（基线 CS1996 优先）。</summary>
    public const string AwaitInLockMirror = "CS2J2930";

    /// <summary>锁对象身份审计：this/typeof/string/Type 变量。</summary>
    public const string LockIdentityAudit = "CS2J2931";

    /// <summary>volatile 非基元字段（转换器注释的诊断化）。</summary>
    public const string VolatileNonPrimitiveField = "CS2J2940";

    /// <summary>System.Threading.Volatile 静态 API 命中（VarHandle 缺口登记）。</summary>
    public const string VolatileStaticApi = "CS2J2960";

    /// <summary>范围外同步原语命中（Mutex/SemaphoreSlim/EventWaitHandle/…），只盘点。</summary>
    public const string OutOfScopePrimitive = "CS2J2961";

    /// <summary>残余闸门：无 plan 归属、无 skip 诊断的 Monitor 绑定站点（strict Error）。</summary>
    public const string ResidualMonitorGate = "CS2J2970";

    /// <summary>区域已改写（仅 verbose）。</summary>
    public const string RegionRewritten = "CN001";

    /// <summary>Wait 谓词循环/虚假唤醒与超时 bool 近似说明（仅 verbose）。</summary>
    public const string WaitSpuriousWakeup = "CN002";
}

/// <summary>
/// C29 诊断记录。MSBuild 格式（设计 §6）：
/// <c>文件(行,列): warning/error CS2J29xx: [CN-x/方法限定名] 描述</c>。
/// </summary>
public sealed record ConcurrencyDiagnostic(
    ConcurrencySeverity Severity,
    ConcurrencyRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? MethodQualifiedName = null)
{
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            ConcurrencySeverity.Error => "error",
            ConcurrencySeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = MethodQualifiedName != null
            ? $"[{Rule.ToDisplay()}/{MethodQualifiedName}] "
            : $"[{Rule.ToDisplay()}] ";
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}
