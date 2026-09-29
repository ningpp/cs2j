namespace CSharpToJava.Core.MemberDesugar;

public enum MemberDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J51xx 诊断码。5100-5109 共享基建；5110-5129 property（C11）；
/// 5130-5189 为 C12/C13/C14 预留。码位只追加不改号（设计 §6）。</summary>
public enum MemberDesugarDiagnosticCode
{
    // 5100-5109 shared infrastructure
    WholeTreeRollback = 5100,       // 新增错误无法归属单一 plan → 全量回退
    PlanRolledBack = 5101,          // 新增错误归属某 plan → 原子回退（也用于本地校验失败）
    RestoreFailed = 5102,
    BuildFailed = 5103,
    TestsFailed = 5104,
    SingleFileNotSelfContained = 5109,
    // 5110-5129 property rules
    PlanCreated = 5110,             // Info，仅 verbose
    BackingFieldGenerated = 5111,   // Info，仅 verbose
    ResidualProperty = 5112,        // PD-12 residual 闸门
    AttributeUnroutable = 5113,     // PD-10
    PartialProperty = 5114,
    RefReturningProperty = 5115,
    StaticAbstractInterfaceProperty = 5116,
    ExpressionTreeSite = 5117,      // force 可解除
    DynamicSite = 5118,
    NonLiftableInitializer = 5119,  // 对象初始化器/复合赋值不可提升
    InitWidened = 5120,             // Info
    RequiredDropped = 5121,         // Info
    FieldKeywordAccessor = 5122,
    PositionalRecordProperty = 5123,
    WithSite = 5124,
    // 5130-5141 indexer rules (C12, 设计 §6)。
    IndexerPlanCreated = 5130,        // Info，仅 verbose
    IndexerNameAdopted = 5131,        // Info：[IndexerName] 自定义名采用（特性已移除）
    IndexerInitWidened = 5132,        // Info：init 归约为普通 set
    DefaultMemberRetained = 5133,     // Info：类型级 [DefaultMember] 保留
    ExternalIndexerSite = 5134,       // Info，仅 verbose：外部索引器用点未改写
    IndexerExpressionTreeSite = 5135, // Warning，force 可解除
    IndexerDynamicSite = 5136,        // Warning
    IndexerNonLiftableSite = 5137,    // Warning：复合/自增/初始化器/写值消费不可提升
    IndexerPartialDeclaration = 5138, // Warning：C# 13 partial 索引器
    IndexerUnsupportedDeclaration = 5139, // Warning：extern / static abstract 接口索引器
    IndexerNameCollision = 5140,      // Warning，防御性
    ResidualIndexer = 5141,           // Warning（strict 升级 Error）：residual 闸门
    // 5150-5158 event rules (C13)。
    EventPlanCreated = 5150,        // Info，仅 verbose
    EventCasPolicyApplied = 5151,   // Info，仅 verbose
    EventStructCasDegraded = 5152,  // struct 事件降级 Simple（策略显式要求 CAS 时升 Warning）
    EventUnsupportedDeclaration = 5153,
    EventDynamicSite = 5154,
    EventUnbindableSite = 5155,
    EventAttributeUnroutable = 5156,
    EventNameCollision = 5157,
    ResidualEvent = 5158,
    // 5170-5183 operator rules (C14)。
    OperatorPlanCreated = 5170,        // Info，仅 verbose
    ConversionPlanCreated = 5171,      // Info，仅 verbose
    OperatorNameDisambiguated = 5172,  // Info，仅 verbose
    OperatorExpressionTreeSite = 5173, // Warning（strict Error）；force 可解除
    OperatorDynamicSite = 5174,        // Warning（strict Error）；force 不解除
    StaticAbstractInterfaceOperator = 5175, // Warning（strict Error）
    UnbuildableLift = 5176,            // Warning（strict Error）
    ConversionMirrorMiss = 5177,       // Warning（strict Error）
    OperatorNameExhausted = 5178,      // Warning（strict Error），理论兜底
    CompoundTargetUnsupported = 5179,  // Warning（strict Error）
    GateOperatorRetained = 5180,       // Info：门控类型 & / | 保留供 JK
    IncrementDecrementDeferred = 5181, // Info：移交 C17
    MetadataOnlyOperatorSite = 5182,   // Info：元数据-only 站点不动
    ResidualOperator = 5183,           // Warning（strict Error）：residual 闸门（OC-9）
}

public sealed record MemberDesugarDiagnostic(
    MemberDesugarSeverity Severity,
    MemberDesugarDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? SymbolName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning CS2J51xx: [Symbol] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            MemberDesugarSeverity.Error => "error",
            MemberDesugarSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = SymbolName != null ? $"[{SymbolName}] " : string.Empty;
        return $"{loc}{level} CS2J{(int)Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器（设计 §5.1 摘要行）。</summary>
public sealed class MemberDesugarStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int PropertiesPlanned { get; set; }
    public int BackingFieldsGenerated { get; set; }
    public int ReadSitesRewritten { get; set; }
    public int WriteSitesRewritten { get; set; }
    public int CompoundSitesRewritten { get; set; }
    public int InitializersRewritten { get; set; }
    public int NameofLiteralized { get; set; }
    public int InitAccessorsWidened { get; set; }
    public int RequiredConstraintsDropped { get; set; }
    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }

    // C12 indexer rule counters (设计 §5.3).
    public int IndexersPlanned { get; set; }
    public int IndexerReadSitesRewritten { get; set; }
    public int IndexerWriteSitesRewritten { get; set; }
    public int IndexerCompoundSitesExpanded { get; set; }
    public int IndexerInitializerSitesRewritten { get; set; }
    public int ExplicitInterfaceIndexers { get; set; }
    public int ExternalIndexerSites { get; set; }
    // ---- C13 events ----
    public int EventsPlanned { get; set; }
    public int EventBackingFieldsGenerated { get; set; }
    public int EventSubscribeSitesRewritten { get; set; }
    public int EventUnsubscribeSitesRewritten { get; set; }
    public int EventInClassCombineSitesRewritten { get; set; }
    public int EventNameofLiteralized { get; set; }
    // C14 operators（设计 §3.2 post-merge 追加清单）
    public int OperatorsPlanned { get; set; }
    public int OperatorBinarySitesRewritten { get; set; }
    public int OperatorUnarySitesRewritten { get; set; }
    public int OperatorCompoundSitesRewritten { get; set; }
    public int OperatorConversionSitesRewritten { get; set; }
    public int OperatorLiftedSitesExpanded { get; set; }
    public int GateOperatorsRetained { get; set; }
    public int IncrementDecrementDeferred { get; set; }
    public int MetadataOperatorSitesUntouched { get; set; }

    /// <summary>按诊断码分桶的跳过数（设计 §6：按原因分组）。</summary>
    public Dictionary<MemberDesugarDiagnosticCode, int> SkippedByReason { get; } = new();
}

/// <summary>Overrides 形态（设计 §5.2）：skip/force 接受
/// "Ns.Type"（整类型）或 "Ns.Type::Prop"（单属性）。</summary>
public sealed class MemberDesugarOptions
{
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>force：仅解除表达式树项的保守跳过（CS2J5117）。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时把 Warning 升级 Error（链内默认 false）。</summary>
    public bool Strict { get; init; }

    public bool ReportInfo { get; init; } = true;

    /// <summary>事件生成访问器的订阅线程安全模式（C13 D5，默认 Simple——与 Roslyn
    /// C# 3.0+ field-like event 的实际代码生成一致；InterlockedCompareExchange 为增强选项）。</summary>
    public EventSubscriptionPolicy EventSubscriptionPolicy { get; init; }
        = EventSubscriptionPolicy.Simple;
}

/// <summary>事件生成访问器的订阅线程安全模式（C13 D5）。Simple 与 Roslyn（C# 3.0+）
/// field-like event 的实际代码生成严格一致；InterlockedCompareExchange 为 lock-free 线性化增强。</summary>
public enum EventSubscriptionPolicy
{
    Simple,
    InterlockedCompareExchange,
}

public sealed record MemberDesugarFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
