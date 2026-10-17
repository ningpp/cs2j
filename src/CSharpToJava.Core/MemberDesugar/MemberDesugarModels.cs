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
    // C11 v1 语义为「field 访问器不收编，plan 跳过」；C50 落地后重定义为 FK-9 residual
    // 闸门码：输出树残留未归一 field token 且无对应 skip 诊断（strict 升 Error）。
    // C50 子集开关关闭时仍以此码承担声明级跳过（零 diff 行为不变）。
    FieldKeywordAccessor = 5122,
    PositionalRecordProperty = 5123,
    WithSite = 5124,
    // ---- C50 field keyword（C# 14 half-implemented properties，设计 §6）----
    FieldKeywordNormalized = 5125,       // Info：field-backed 属性归一留痕（verbose）
    FieldKeywordSafetyNet = 5126,        // Warning（strict Error）：field 文本在但绑不到隐式后备字段
    FieldKeywordUnsupportedContainer = 5127, // Warning（strict Error）：不收编容器（接口实例 DIM 等）
    FieldKeywordAttributeFailed = 5128,  // Warning（strict Error）：[field:] 特性迁移失败
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
    // 5212-5230 record `with` rules (C27, 设计 §6)。M1 落地 RC-1/RC-2 + WR-1/2/3/6/7 主体；
    // 标注 M2 的码位本阶段仅作登记/占位（struct 值拷贝、位置 reshape、嵌套 with 等）。
    WithSiteRewritten = 5212,              // Info，仅 verbose：with 站点已改写
    CloneFactorySynthesized = 5213,        // Info，仅 verbose：克隆工厂已合成
    PositionalReshaped = 5214,             // Info：位置 record reshape 完成（M2）
    RequiredRecordTouched = 5215,          // Info：record 面 required/SetsRequiredMembers（M2/M3）
    PositionalReshapeGate = 5220,          // Warning（strict Error）：位置 reshape 闸门（M2）；
                                           // M1 兼作「留待 M2 形态」的统一门码（位置 record/
                                           // struct 值拷贝/init-only 未放宽等），消息逐形注明
    MetadataDerivedRisk = 5221,            // Warning（strict Error）：树外派生 record 虚分派洞
    MetadataRecordReceiver = 5222,         // Warning（strict Error）：元数据 record 接收者
    NonLiftableWithContext = 5223,         // Warning（strict Error）：with 位于不可提升上下文
    UnsupportedNestedInitializer = 5224,   // Warning（strict Error）：嵌套对象/集合初始化器/嵌套 with（M2）
    AnonymousTypeWith = 5225,              // Warning（strict Error）：匿名类型 with
    ExpressionTreeWith = 5226,             // Warning（strict Error）：表达式树内 with
    DynamicWith = 5227,                    // Warning（strict Error）：dynamic 接收者 with
    NonRecordClassWith = 5228,             // Warning（strict Error）：非 record class 接收者 with
    ResidualWith = 5230,                   // Warning（strict Error）：residual 零 with 闸门（WR-8）
    RecordSingleFileNotSelfContained = 5290, // Warning：单文件模式 record plan 非自包含
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

    // ---- C50 field keyword（设计 §5 摘要字段）----
    /// <summary>归一的 field-backed 属性数。</summary>
    public int FieldKeywordProperties { get; set; }
    /// <summary>展开的无体 auto 访问器数（get;/set;/init;）。</summary>
    public int FieldKeywordAutoAccessorsExpanded { get; set; }
    /// <summary>替换为契约字段名的 field 标识符（FieldExpression）节点数。</summary>
    public int FieldReferencesRewritten { get; set; }
    /// <summary>迁移到合成字段的 [field:] 特性数。</summary>
    public int FieldTargetAttributesMigrated { get; set; }
    /// <summary>平移到合成字段的属性初始化器数。</summary>
    public int FieldInitializersMoved { get; set; }
    /// <summary>FK-9 residual：无对应 skip 诊断的残留 field token 数（正常恒为 0）。</summary>
    public int FieldKeywordResidualTokens { get; set; }

    // ---- C27 record `with`（M1：显式成员 record class，设计 §5.3）----
    /// <summary>检出的 record 克隆 plan 总数（含 Infeasible；仅首个声明文件计入）。</summary>
    public int RecordPlans { get; set; }
    /// <summary>其中 record class（可发克隆工厂）plan 数。</summary>
    public int RecordClassPlans { get; set; }
    /// <summary>实际合成的克隆工厂方法数（abstract/virtual/override/sealed 全形态）。</summary>
    public int CloneFactoriesSynthesized { get; set; }
    /// <summary>重写的 with 站点总数。</summary>
    public int WithSitesTotal { get; set; }
    /// <summary>其中 record class 工厂形态站点数（含多态 WR-2、类型内 WR-6）。</summary>
    public int WithSitesClass { get; set; }
    /// <summary>其中 struct/record struct/tuple 值拷贝形态站点数（WR-4，M2）。</summary>
    public int WithSitesStruct { get; set; }
    /// <summary>其中空初始化器站点数（WR-3，纯工厂内联；struct 保守值拷贝）。</summary>
    public int WithSitesEmpty { get; set; }
    /// <summary>实际 reshape 的位置 record 类型数（RC-3/RC-4，CS2J5214，M2）。</summary>
    public int PositionalRecordsReshaped { get; set; }
    /// <summary>提升的源/克隆临时变量数（__cs2j_wsrc/__cs2j_wclone）。</summary>
    public int TemporariesPromoted { get; set; }
    /// <summary>with 覆写中经 set_X 发射的属性赋值数（M2 位置 reshape/5124 退役后为主路径）。</summary>
    public int PropertyAssignments { get; set; }
    /// <summary>with 覆写中直写字段的赋值数。</summary>
    public int FieldAssignments { get; set; }
    /// <summary>WR-8 residual：输出树残留应被认领却未重写的 with 数（正常恒为 0）。</summary>
    public int ResidualWithCount { get; set; }

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

    // ---- C46 signatures（M2 改写计数，设计 §5.2）----
    /// <summary>已提升的显式接口实现数（去限定名 + 提 public，SI-1）。</summary>
    public int SignaturePlansPromoted { get; set; }
    /// <summary>被让位改名的类自有同槽成员数（SI-2）。</summary>
    public int SignatureDisplacedRenamed { get; set; }
    /// <summary>同名多接口体等价合并数（SI-3）。</summary>
    public int SignatureDualInterfaceMerged { get; set; }
    /// <summary>重写的调用点数（含 nameof 字面量化，SI-5）。</summary>
    public int SignatureCallSitesRewritten { get; set; }
    /// <summary>residual 对账 CS2J5199 条数（SI-7，正常恒为 0）。</summary>
    public int SignatureResidual { get; set; }
    /// <summary>反射/字符串契约风险站点告警数（CS2J5193，非阻断）。</summary>
    public int SignatureReflectionWarnings { get; set; }

    // ---- C46 signatures（M3/M4a 隐藏改写计数，设计 §5.2）----
    /// <summary>改名的隐藏成员声明数（NH-1 实例化石/NH-2 newslot 链/NH-4 字段与静态方法，
    /// 均改 <c>{Name}_cs2jNew</c>）。</summary>
    public int SignatureHideRenamed { get; set; }
    /// <summary>NH-3 保留的同名 public 桥接数（纯 base 委托白名单，记 Bridged）。</summary>
    public int SignatureHideBridged { get; set; }

    // ---- C46 signatures（M4b 协变桥接改写计数，设计 §5.2）----
    /// <summary>CR-2 合成的擦除冲突协变 bridge 数（基槽签名 override 壳）。</summary>
    public int SignatureCovariantBridgesSynthesized { get; set; }
    /// <summary>CR-2 改名的协变声明数（bridge 点 + 编译内派生 sub-chain）。</summary>
    public int SignatureCovariantRenamed { get; set; }

    /// <summary>按诊断码分桶的跳过数（设计 §6：按原因分组）。</summary>
    public Dictionary<MemberDesugarDiagnosticCode, int> SkippedByReason { get; } = new();

    /// <summary>逐文件改写产出并入时的计数器求和（并行改写路径串行合并用，设计 §4.2）。
    /// 全部计数器均为加性；<see cref="FinalizerSidecarPath"/> 为 run 级单值，取非 null。</summary>
    public void Add(MemberDesugarStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        PropertiesPlanned += other.PropertiesPlanned;
        BackingFieldsGenerated += other.BackingFieldsGenerated;
        ReadSitesRewritten += other.ReadSitesRewritten;
        WriteSitesRewritten += other.WriteSitesRewritten;
        CompoundSitesRewritten += other.CompoundSitesRewritten;
        InitializersRewritten += other.InitializersRewritten;
        NameofLiteralized += other.NameofLiteralized;
        InitAccessorsWidened += other.InitAccessorsWidened;
        RequiredConstraintsDropped += other.RequiredConstraintsDropped;
        PlansSkipped += other.PlansSkipped;
        PlansRolledBack += other.PlansRolledBack;
        IndexersPlanned += other.IndexersPlanned;
        IndexerReadSitesRewritten += other.IndexerReadSitesRewritten;
        IndexerWriteSitesRewritten += other.IndexerWriteSitesRewritten;
        IndexerCompoundSitesExpanded += other.IndexerCompoundSitesExpanded;
        IndexerInitializerSitesRewritten += other.IndexerInitializerSitesRewritten;
        ExplicitInterfaceIndexers += other.ExplicitInterfaceIndexers;
        ExternalIndexerSites += other.ExternalIndexerSites;
        EventsPlanned += other.EventsPlanned;
        EventBackingFieldsGenerated += other.EventBackingFieldsGenerated;
        EventSubscribeSitesRewritten += other.EventSubscribeSitesRewritten;
        EventUnsubscribeSitesRewritten += other.EventUnsubscribeSitesRewritten;
        EventInClassCombineSitesRewritten += other.EventInClassCombineSitesRewritten;
        EventNameofLiteralized += other.EventNameofLiteralized;
        OperatorsPlanned += other.OperatorsPlanned;
        OperatorBinarySitesRewritten += other.OperatorBinarySitesRewritten;
        OperatorUnarySitesRewritten += other.OperatorUnarySitesRewritten;
        OperatorCompoundSitesRewritten += other.OperatorCompoundSitesRewritten;
        OperatorConversionSitesRewritten += other.OperatorConversionSitesRewritten;
        OperatorLiftedSitesExpanded += other.OperatorLiftedSitesExpanded;
        GateOperatorsRetained += other.GateOperatorsRetained;
        IncrementDecrementDeferred += other.IncrementDecrementDeferred;
        MetadataOperatorSitesUntouched += other.MetadataOperatorSitesUntouched;
        FinalizersDetected += other.FinalizersDetected;
        FinalizersReported += other.FinalizersReported;
        FinalizerCriticalBlocked += other.FinalizerCriticalBlocked;
        FinalizerResurrectionSites += other.FinalizerResurrectionSites;
        FinalizerSuppressFinalizeSites += other.FinalizerSuppressFinalizeSites;
        FinalizerChainsWithBaseFinalizer += other.FinalizerChainsWithBaseFinalizer;
        FinalizerEmpty += other.FinalizerEmpty;
        FinalizerDisposePattern += other.FinalizerDisposePattern;
        FinalizerSafeHandleOnly += other.FinalizerSafeHandleOnly;
        FinalizerNativeRelease += other.FinalizerNativeRelease;
        FinalizerOther += other.FinalizerOther;
        FinalizerExtern += other.FinalizerExtern;
        FinalizersRemoved += other.FinalizersRemoved;
        FinalizersForceRemoved += other.FinalizersForceRemoved;
        FinalizersRefused += other.FinalizersRefused;
        FinalizerSidecarEntries += other.FinalizerSidecarEntries;
        FinalizerResidual += other.FinalizerResidual;
        FieldKeywordProperties += other.FieldKeywordProperties;
        FieldKeywordAutoAccessorsExpanded += other.FieldKeywordAutoAccessorsExpanded;
        FieldReferencesRewritten += other.FieldReferencesRewritten;
        FieldTargetAttributesMigrated += other.FieldTargetAttributesMigrated;
        FieldInitializersMoved += other.FieldInitializersMoved;
        FieldKeywordResidualTokens += other.FieldKeywordResidualTokens;
        RecordPlans += other.RecordPlans;
        RecordClassPlans += other.RecordClassPlans;
        CloneFactoriesSynthesized += other.CloneFactoriesSynthesized;
        WithSitesTotal += other.WithSitesTotal;
        WithSitesClass += other.WithSitesClass;
        WithSitesStruct += other.WithSitesStruct;
        WithSitesEmpty += other.WithSitesEmpty;
        PositionalRecordsReshaped += other.PositionalRecordsReshaped;
        TemporariesPromoted += other.TemporariesPromoted;
        PropertyAssignments += other.PropertyAssignments;
        FieldAssignments += other.FieldAssignments;
        ResidualWithCount += other.ResidualWithCount;
        SignatureExplicitMembersDetected += other.SignatureExplicitMembersDetected;
        SignatureExplicitMethods += other.SignatureExplicitMethods;
        SignatureExplicitProperties += other.SignatureExplicitProperties;
        SignatureExplicitIndexers += other.SignatureExplicitIndexers;
        SignatureExplicitEvents += other.SignatureExplicitEvents;
        SignatureHidingDetected += other.SignatureHidingDetected;
        SignatureHidingNew += other.SignatureHidingNew;
        SignatureHidingImplicit += other.SignatureHidingImplicit;
        SignatureHidingNewSlot += other.SignatureHidingNewSlot;
        SignatureHidingStatic += other.SignatureHidingStatic;
        SignatureCovariantChains += other.SignatureCovariantChains;
        SignatureCovariantRetained += other.SignatureCovariantRetained;
        SignatureCovariantBridged += other.SignatureCovariantBridged;
        SignatureCovariantValueTypeSkipped += other.SignatureCovariantValueTypeSkipped;
        SignaturePlansPromoted += other.SignaturePlansPromoted;
        SignatureDisplacedRenamed += other.SignatureDisplacedRenamed;
        SignatureDualInterfaceMerged += other.SignatureDualInterfaceMerged;
        SignatureCallSitesRewritten += other.SignatureCallSitesRewritten;
        SignatureResidual += other.SignatureResidual;
        SignatureReflectionWarnings += other.SignatureReflectionWarnings;
        SignatureHideRenamed += other.SignatureHideRenamed;
        SignatureHideBridged += other.SignatureHideBridged;
        SignatureCovariantBridgesSynthesized += other.SignatureCovariantBridgesSynthesized;
        SignatureCovariantRenamed += other.SignatureCovariantRenamed;
        foreach (var (code, count) in other.SkippedByReason)
        {
            SkippedByReason.TryGetValue(code, out var existing);
            SkippedByReason[code] = existing + count;
        }
        FinalizerSidecarPath ??= other.FinalizerSidecarPath;
    }
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

    /// <summary>C50 field 关键字归一子节（rules.fieldKeyword，设计 §12 默认关子集开关）。
    /// 默认 <see cref="Properties.FieldKeywordOptions.Enabled"/>=false：field 规则零执行，
    /// 输出与现状逐字节一致；显式启用后才做声明侧归一。</summary>
    public Properties.FieldKeywordOptions FieldKeyword { get; init; } = new();

    /// <summary>C27 record <c>with</c> 去糖子节（rules.records，设计 §12 默认关子集开关）。
    /// 默认 <see cref="Records.RecordOptions.Enabled"/>=false：Records 规则零分析/零 plan/零改写；
    /// 显式启用后才合成克隆工厂并改写 with 站点。</summary>
    public Records.RecordOptions Records { get; init; } = new();
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
