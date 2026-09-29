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
}

public sealed record MemberDesugarFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
