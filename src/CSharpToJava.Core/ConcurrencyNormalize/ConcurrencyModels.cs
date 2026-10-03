using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>Monitor 调用种类（按重载形分）。</summary>
public enum MonitorCallKind
{
    /// <summary>Monitor.Enter(o)。</summary>
    Enter,

    /// <summary>Monitor.Enter(o, ref taken)。</summary>
    EnterWithTaken,

    /// <summary>Monitor.Exit(o)。</summary>
    Exit,

    /// <summary>Monitor.TryEnter(o[, timeout]) 裸 bool 返回形。</summary>
    TryEnter,

    /// <summary>Monitor.TryEnter(o[, timeout], ref taken) lockTaken 形。</summary>
    TryEnterWithTaken,

    /// <summary>Monitor.Wait 各重载。</summary>
    Wait,

    /// <summary>Monitor.Pulse(o)。</summary>
    Pulse,

    /// <summary>Monitor.PulseAll(o)。</summary>
    PulseAll,
}

/// <summary>方法级 region plan 结局：Active=配对类（CN-2/CN-3）可改写；Skipped=配对类整体不动。</summary>
public enum Outcome
{
    Active,
    Skipped,
}

/// <summary>
/// 一个语义绑定命中的 Monitor 调用站点（GetSymbolInfo + IMethodSymbol 身份判定，
/// 禁止文本匹配）。
/// </summary>
public sealed class MonitorCallSite
{
    public required MonitorCallKind Kind { get; init; }

    /// <summary>原始树中的调用节点（plan 构建期持有，重写前有效）。</summary>
    public required InvocationExpressionSyntax Node { get; init; }

    public required TextSpan Span { get; init; }

    public required string FilePath { get; init; }

    public required string MethodQualifiedName { get; init; }

    /// <summary>重载参数形摘要，如 "(object, ref bool)"。</summary>
    public required string OverloadShape { get; init; }

    public int Line => Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public int Column => Node.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
}

internal enum WaitOverloadKind
{
    NoTimeout,
    IntMs,
    TimeSpan,
    IntMsExitContext,
    TimeSpanExitContext,
}

/// <summary>CN-2 改写单元：一组可整体归一为 lock 的 Enter/Exit 配对。</summary>
internal sealed class EnterExitPairUnit
{
    public required ConcurrencyRegionPlan Plan { get; init; }

    /// <summary>形 A（含 ref taken）；false 为形 B（旧式 Enter）。</summary>
    public required bool IsFormA { get; init; }

    /// <summary>形 A 变体：Enter 为 try 块首语句（编译器降低形）。</summary>
    public required bool EnterInsideTry { get; init; }

    public required InvocationExpressionSyntax EnterInvocation { get; init; }
    public required ExpressionSyntax LockExpression { get; init; }
    public ISymbol? LockSymbol { get; init; }
    public required TryStatementSyntax TryStatement { get; init; }

    /// <summary>锁块 BODY（原样入 lock；Enter-inside-try 形已剔除首条 Enter 语句）。</summary>
    public required IReadOnlyList<StatementSyntax> BodyStatements { get; init; }

    public required InvocationExpressionSyntax ExitInvocation { get; init; }
    public LocalDeclarationStatementSyntax? TakenDeclaration { get; init; }
    public ILocalSymbol? TakenLocal { get; init; }

    /// <summary>锚节点（taken 声明或 Enter 语句，即首个被消费语句；无语句宿主时为调用节点本身）。</summary>
    public required SyntaxNode AnchorStatement { get; init; }
    public required TextSpan AnchorSpan { get; init; }
    public required int ConsumedStatements { get; init; }
    public required bool Feasible { get; init; }
    public string? FailReason { get; init; }

    /// <summary>LockRewriter 状态：本单元已被消费。</summary>
    public bool Handled;

    /// <summary>LockRewriter 状态：本单元已被实际改写（区别于仅消费/已规范 noop）。</summary>
    public bool Rewritten;
}

/// <summary>CN-3 改写单元：一个 TryEnter 站点到规范形的归一。</summary>
internal sealed class TryEnterUnit
{
    public required ConcurrencyRegionPlan Plan { get; init; }

    /// <summary>裸 bool 形：if 条件直接调用 TryEnter。</summary>
    public required bool IsBareBoolIf { get; init; }

    /// <summary>lockTaken 形（含 ref taken 实参）。</summary>
    public required bool IsRefTaken { get; init; }

    /// <summary>已是 3 参规范形：只做结构校验，不改写。</summary>
    public required bool IsCanonicalThreeArg { get; init; }

    public required InvocationExpressionSyntax Invocation { get; init; }
    public required ExpressionSyntax LockExpression { get; init; }
    public ISymbol? LockSymbol { get; init; }

    /// <summary>超时实参（int/TimeSpan）；无超时形归一时补 0。</summary>
    public ExpressionSyntax? TimeoutArgument { get; init; }
    public bool TimeoutIsTimeSpan { get; init; }
    public ILocalSymbol? TakenLocal { get; init; }
    public LocalDeclarationStatementSyntax? TakenDeclaration { get; init; }
    public IfStatementSyntax? IfStatement { get; init; }
    public TryStatementSyntax? TryStatement { get; init; }

    /// <summary>被守卫的 BODY（裸 bool 形为原 try 块语句；ref 形为 try 块语句）。</summary>
    public IReadOnlyList<StatementSyntax>? GuardedBodyStatements { get; init; }
    public InvocationExpressionSyntax? ExitInvocation { get; init; }
    public StatementSyntax? ElseStatement { get; init; }

    public required SyntaxNode AnchorStatement { get; init; }
    public required TextSpan AnchorSpan { get; init; }
    public required int ConsumedStatements { get; init; }
    public required bool Feasible { get; init; }
    public string? FailReason { get; init; }

    // 输入形分桶标志（同一站点可同时命中多个桶）
    public bool IsTimed { get; init; }
    public bool IsZeroTimeout { get; init; }
    public bool IsLockTaken { get; init; }
    public bool IsBareBool { get; init; }

    /// <summary>lockTaken 语句形中包含 TryEnter 调用的语句（ref 形改写时替换该语句）。</summary>
    public ExpressionStatementSyntax? CallStatement { get; init; }

    /// <summary>LockRewriter 状态：本单元已被消费。</summary>
    public bool Handled;

    /// <summary>LockRewriter 状态：本单元已被实际改写（区别于仅消费/已规范 noop）。</summary>
    public bool Rewritten;
}

/// <summary>CN-4 单站点改写单元：一个 Wait 调用的重载归一（独立于配对类原子性）。</summary>
internal sealed class WaitUnit
{
    public required ConcurrencyRegionPlan Plan { get; init; }
    public required WaitOverloadKind Overload { get; init; }
    public required InvocationExpressionSyntax Invocation { get; init; }

    /// <summary>TimeSpan 实参表达式（提升 int 毫秒临时量后转 int 重载）。</summary>
    public ExpressionSyntax? TimeSpanArgument { get; init; }

    /// <summary>exitContext 实参下标（无则 -1）。</summary>
    public int ExitContextArgumentIndex { get; init; } = -1;

    /// <summary>exitContext 为常量 false → 折叠为 int 重载。</summary>
    public bool ExitContextFoldable { get; set; }

    /// <summary>exitContext 常量 true/变量 → CS2J2921，站点原样。</summary>
    public bool SkipRewrite { get; set; }

    public required SyntaxNode AnchorStatement { get; init; }
    public required TextSpan AnchorSpan { get; init; }

    /// <summary>LockRewriter 状态：本单元已被消费。</summary>
    public bool Handled;
}

/// <summary>
/// 方法级原子 region plan（设计 §4.2）：每个含 Monitor 站点的普通方法一条。
/// 配对类（CN-2/CN-3）判定为 plan 级——任一不可行站点则该方法配对类整体不动；
/// Wait/Pulse 规范化是单站点独立的，可分别改写。
/// </summary>
public sealed class ConcurrencyRegionPlan
{
    /// <summary>确定性 Id："CN:方法限定名@方法起始位置"。</summary>
    public required string Id { get; init; }

    public required IMethodSymbol Method { get; init; }
    public required string MethodQualifiedName { get; init; }

    /// <summary>plan 主文件（方法声明所在文件，用于确定性排序）。</summary>
    public required string PrimaryFile { get; init; }
    public required int MethodSpanStart { get; init; }

    public required string ContainingTypeName { get; init; }
    public required string MethodName { get; init; }

    public Outcome Outcome { get; set; } = Outcome.Active;
    public string? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }

    /// <summary>配对类（CN-2/CN-3）整体是否可改写。</summary>
    public bool PairClassActive => Outcome == Outcome.Active;

    /// <summary>方法内全部 Monitor 站点。</summary>
    public List<MonitorCallSite> Sites { get; } = new();

    internal List<EnterExitPairUnit> PairUnits { get; } = new();
    internal List<TryEnterUnit> TryEnterUnits { get; } = new();
    internal List<WaitUnit> WaitUnits { get; } = new();

    /// <summary>锁对象绑定摘要（"表达式 → 符号种类 显示名"）。</summary>
    public List<string> LockObjectBindings { get; } = new();

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>方法内已用标识符（合成临时量 __cs2j_mt/__cs2j_to 碰撞加序号用）。</summary>
    internal HashSet<string> UsedIdentifiers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 整树重编译新增错误归属：文件 + 所在类型/成员名对 plan 触碰面求唯一归属
    /// （同 RR OwnsTouch 口径）。
    /// </summary>
    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(file)) return false;
        if (typeName != null
            && !string.Equals(typeName, ContainingTypeName, StringComparison.Ordinal)
            && !MethodQualifiedName.Contains(typeName, StringComparison.Ordinal))
            return false;
        if (memberName != null
            && !string.Equals(memberName, MethodName, StringComparison.Ordinal)
            && !MethodQualifiedName.Contains(memberName, StringComparison.Ordinal))
            return false;
        return true;
    }
}

/// <summary>整树一次分析的结果：plan 集 + 全局审计诊断/计数（CN-4/5/6/7/9/10/11）。</summary>
internal sealed class ConcurrencyAnalysis
{
    public List<ConcurrencyRegionPlan> Plans { get; } = new();
    public List<ConcurrencyDiagnostic> Diagnostics { get; } = new();
    public Dictionary<string, int> IdentityAuditsByKind { get; } = new();
    public Dictionary<string, int> OutOfScopeSitesByType { get; } = new();
    public int WaitSites;
    public int WaitSitesNoTimeout;
    public int WaitSitesIntMs;
    public int WaitSitesTimeSpan;
    public int WaitSitesExitContext;
    public int PulseSites;
    public int PulseAllSites;
    public int VolatileFieldsPrimitive;
    public int VolatileFieldsNonPrimitive;
    public int VolatileStaticApiSites;
    public int OwnershipWarnings;
    public int InfeasibleSites;
}

/// <summary>逐文件改写结果；Changed=false 时 Preprocessor 直接返回 null，runner 字节级复制。</summary>
public sealed record ConcurrencyFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}

/// <summary>语法/命名小工具：改动节点注解与合成临时量确定性基名。</summary>
internal static class ConcurrencySyntaxFacts
{
    /// <summary>改动节点统一挂该注解（FormatAndValidate 局部格式化区间定位用）。</summary>
    public static readonly SyntaxAnnotation RewriteAnnotation = new("Cs2jConcurrencyNormalize");

    /// <summary>lockTaken 合成临时量基名（碰撞加序号：__cs2j_mt2、__cs2j_mt3…）。</summary>
    public const string TempTakenName = "__cs2j_mt";

    /// <summary>TimeSpan→int 毫秒合成临时量基名（碰撞加序号：__cs2j_to2…）。</summary>
    public const string TempTimeoutName = "__cs2j_to";
}
