namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// C29 统计计数器（设计 §4.1 字段全集）。检测侧计数由 Analyzer 一次成型后并入，
/// 改写侧计数由 LockRewriter 在逐文件改写时累加；Statistics 实例由 CLI 适配器
/// 持有并做链尾汇总（同 RR/枚举先例的通用投影形状）。
/// </summary>
public sealed class ConcurrencyStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    // CN-1：lock 语句保留直映，仅计数
    public int LockStatementsSeen { get; set; }
    public int LockNestedRegions { get; set; }

    // CN-2：Enter/Exit 配对
    public int EnterExitPairsDetected { get; set; }
    public int EnterExitPairsRewritten { get; set; }
    public int EnterExitPairsRejected { get; set; }

    // CN-3：TryEnter 站点（按输入形分桶，同一站点可同时命中多个桶）
    public int TryEnterSitesDetected { get; set; }
    public int TryEnterSitesCanonicalized { get; set; }
    public int TryEnterTimed { get; set; }
    public int TryEnterZeroTimeout { get; set; }
    public int TryEnterLockTaken { get; set; }
    public int TryEnterBareBool { get; set; }

    // CN-4：Wait 站点（按重载形分桶）
    public int WaitSites { get; set; }
    public int WaitSitesNoTimeout { get; set; }
    public int WaitSitesIntMs { get; set; }
    public int WaitSitesTimeSpan { get; set; }
    public int WaitSitesExitContext { get; set; }
    public int WaitSitesCanonicalized { get; set; }

    // CN-5：Pulse/PulseAll（只做持锁校验与计数）
    public int PulseSites { get; set; }
    public int PulseAllSites { get; set; }

    // CN 锁所有权判定失败（CS2J2920）计数
    public int OwnershipWarnings { get; set; }

    // CN-6：锁对象身份审计，按 kind（This/TypeOf/String/TypeVariable）分桶
    public Dictionary<string, int> IdentityAuditsByKind { get; } = new();

    // CN-7：volatile 字段分类
    public int VolatileFieldsPrimitive { get; set; }
    public int VolatileFieldsNonPrimitive { get; set; }

    // CN-9：System.Threading.Volatile 静态 API 站点
    public int VolatileStaticApiSites { get; set; }

    // CN-11：范围外同步原语，按类型名分桶
    public Dictionary<string, int> OutOfScopeSitesByType { get; } = new();

    // CS2J2910 非规范结构站点计数
    public int InfeasibleSites { get; set; }

    /// <summary>按诊断码分桶的跳过数（2910/2921/2930：区域原样不动的痕迹）。</summary>
    public Dictionary<string, int> SkippedByReason { get; } = new();

    // 残余闸门：输出树 Monitor 绑定站点总数
    public int ResidualMonitorCalls { get; set; }
}
