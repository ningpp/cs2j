namespace CSharpToJava.Core.MemberDesugar;

public enum MemberDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J51xx 诊断码。5100-5109 共享基建；5110-5129 property（C11）；
/// 5130-5189 为 C12/C13/C14 预留；5190-5199 留 C46；5200-5219 为 C39 终结器规则段。
/// 码位只追加不改号（设计 §6 / C39 设计 §6）。</summary>
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
    // 5190-5199 signature rules (C46, 设计 §6)。C46 独占，C39 禁占。
    SignaturePlanCreated = 5190,            // Info，仅 verbose：签名 plan/审计建立
    SignatureDivergentInterfaceBodies = 5191, // Warning（strict Error）：同名不同接口实现体分歧（SI-3）
    SignatureNameReservationConflict = 5192,  // Warning（strict Error）：命名预留/让位者不可确定，plan 跳过
    SignatureReflectionContract = 5193,       // Warning（非阻断）：反射/字符串契约风险站点，站点保留不改
    SignatureExpressionTreeOrDynamic = 5194,  // Warning（strict Error）：表达式树（force 不解除）/dynamic 站点不可判定
    SignatureValueTypeCovariant = 5195,       // Warning（strict Error）：值类型/不可证 Java 可表达协变（CR-3）
    SignatureGenericBridgeInfeasible = 5196,  // Warning（strict Error）：泛型擦除协变桥接 v1 不可行
    SignatureSlotAmbiguous = 5197,            // Warning（strict Error）：接口槽位映射候选不唯一（SI-4）
    SignatureStaticAbstractInfo = 5198,       // Info：命中 static abstract/virtual 接口成员，归 C36/CS2J0585
    SignatureResidual = 5199,                 // Warning（strict Error）：residual 闸门（SI-7/NH-5/CR-4）
    // 5200-5211 finalizer rules (C39, 设计 §6)。M1 仅 A 档显式诊断；
    // 5201/5204/5210 为 M2 删除模式/residual 预留码位（本阶段不发射）。
    FinalizerRejected = 5200,              // Warning（strict Error）：A 档显式拒绝，成员保留
    FinalizerRemoved = 5201,               // Warning：B 档已删除 + sidecar（M2）
    FinalizerDisposePattern = 5202,        // Info，仅 verbose：标准 Dispose 模式 + 配对
    FinalizerRedundant = 5203,             // Info，仅 verbose：空体/仅 SafeHandle 字段，冗余
    FinalizerRemoveRefused = 5204,         // Warning（strict Error）：B 档拒绝删除（M2）
    FinalizerCriticalBlocked = 5205,       // Error（不可解除）：CriticalFinalizerObject 派生
    FinalizerGcLifecycleSite = 5206,       // Info，仅 verbose：GC 生命周期 API 站点清点
    FinalizerBaseChain = 5207,             // Info，仅 verbose：基类终结器链信息
    FinalizerCleanerGuideUnsupported = 5208, // Error：Policy=CleanerGuide v1 未实现
    FinalizerResidual = 5210,              // Warning（strict Error）：residual 对账不平（M2）
    FinalizerSingleFileDegraded = 5211,    // Warning：单文件模式分析降级
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

    // ---- C39 finalizers（M1：仅显式诊断，设计 §5.3/§9）----
    /// <summary>检出的终结器 plan 数（仅在首个声明文件计入）。</summary>
    public int FinalizersDetected { get; set; }
    /// <summary>发射主报码（5200/5205/5208）的终结器数。</summary>
    public int FinalizersReported { get; set; }
    /// <summary>CriticalFinalizerObject 派生、被 CS2J5205 阻断数。</summary>
    public int FinalizerCriticalBlocked { get; set; }
    /// <summary>含终结器类型内 GC.ReRegisterForFinalize 站点总数。</summary>
    public int FinalizerResurrectionSites { get; set; }
    /// <summary>含终结器类型内 GC.SuppressFinalize 站点总数。</summary>
    public int FinalizerSuppressFinalizeSites { get; set; }
    /// <summary>基类链上存在基类终结器的 plan 数。</summary>
    public int FinalizerChainsWithBaseFinalizer { get; set; }
    // FN-2 六分类计数。
    public int FinalizerEmpty { get; set; }
    public int FinalizerDisposePattern { get; set; }
    public int FinalizerSafeHandleOnly { get; set; }
    public int FinalizerNativeRelease { get; set; }
    public int FinalizerOther { get; set; }
    public int FinalizerExtern { get; set; }

    // ---- C39 finalizers M2（B 档删除，设计 §5.3）----
    /// <summary>B 档实际删除数（含 force；CS2J5201 条数 == sidecar 条目数）。</summary>
    public int FinalizersRemoved { get; set; }
    /// <summary>其中经 ForceRemoveTypes 强制删除数。</summary>
    public int FinalizersForceRemoved { get; set; }
    /// <summary>B 档拒绝删除数（CS2J5204，成员保留；5205 另计 FinalizerCriticalBlocked）。</summary>
    public int FinalizersRefused { get; set; }
    /// <summary>sidecar 条目数（恒等于 FinalizersRemoved）。</summary>
    public int FinalizerSidecarEntries { get; set; }
    /// <summary>residual 闸门 CS2J5210 条数（FN-10，正常恒为 0）。</summary>
    public int FinalizerResidual { get; set; }
    /// <summary>实际 sidecar 文件绝对路径（无删除/内存模式为 null；摘要行取此值）。</summary>
    public string? FinalizerSidecarPath { get; set; }

    // ---- C46 signatures（M1 审计计数，设计 §5.2/§9）。改写计数由后续里程碑追加。----
    /// <summary>显式接口实现成员总数（按方法/属性/索引器/事件分桶的合计）。</summary>
    public int SignatureExplicitMembersDetected { get; set; }
    public int SignatureExplicitMethods { get; set; }
    public int SignatureExplicitProperties { get; set; }
    public int SignatureExplicitIndexers { get; set; }
    public int SignatureExplicitEvents { get; set; }
    /// <summary>new 隐藏成员总数（按 new/隐式/newslot/静态分桶的合计）。</summary>
    public int SignatureHidingDetected { get; set; }
    public int SignatureHidingNew { get; set; }       // 显式 new 令牌（非 virtual）
    public int SignatureHidingImplicit { get; set; }  // 无 new 令牌（CS0108 隐式隐藏）
    public int SignatureHidingNewSlot { get; set; }   // new virtual/abstract（另开虚槽链）
    public int SignatureHidingStatic { get; set; }    // 静态成员隐藏
    /// <summary>协变返回 override 链总数（按 retained/bridged/valueTypeSkipped 分桶的合计）。</summary>
    public int SignatureCovariantChains { get; set; }
    public int SignatureCovariantRetained { get; set; }        // CR-1 直映保留
    public int SignatureCovariantBridged { get; set; }         // CR-2 擦除冲突待桥接
    public int SignatureCovariantValueTypeSkipped { get; set; } // CR-3 值类型/不可判

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

    /// <summary>C39 终结器规则子节（rules.finalizers，设计 §2 决策 9）。Strict 沿用族级
    /// <see cref="Strict"/>，不在子节内重复。</summary>
    public Finalizers.FinalizerOptions Finalizers { get; init; } = new();

    /// <summary>C46 签名规则子节（rules.signatures，设计 §5/§12）。族 pass 已 defaultEnabled=true，
    /// 由本子节 <see cref="Signatures.SignatureOptions.Enabled"/>（默认 false）承接「不显式启用即零执行」。</summary>
    public Signatures.SignatureOptions Signatures { get; init; } = new();
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
