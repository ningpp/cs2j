namespace CSharpToJava.Core.StructValueSemantics;

public enum StructValueSemanticsSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>SV0xx 诊断码（设计 §6）。码位只追加不改号。</summary>
public enum StructValueSemanticsDiagnosticCode
{
    // 001-009 转换结果（Info）
    SV001, // struct 已纳入 plan（含 shim 形态/站点数）
    SV010, // 复用用户既有 Clone()，未生成 shim
    SV011, // ref struct 排除（交 C01/C06）
    SV012, // 已 readonly struct 不参与（防御性登记）
    SV013, // 表达式树 lambda 内站点跳过
    SV014, // dynamic 绑定站点跳过

    // 020-029 计划级跳过/回退
    SV020, // plan 原子跳过（原因码随消息；overrides.skip/保守不可证）
    SV030, // 整树新增错误无法归属单一 plan → 本次运行全部回退
    SV031, // 新增错误归属某 plan → 该 plan 原子回退

    // 040-049 语言层面 clone 无法表达值语义（默认 Error）
    SV040, // this 按引用逃逸
    SV041, // 结构体内 this = expr
    SV042, // unsafe/指针/fixed 与值副本边界相交且无法判定；ref struct × struct 混叠
    SV043, // 表达式位置必须提升临时变量而语法上不可提升

    // 050-059 shim 不可行
    SV050, // 既有 Clone()/拷贝构造签名不兼容
    SV051, // struct 含事件字段/fixed 缓冲区等无法逐字段复制的成员

    // 060-069 环境
    SV060, // dotnet restore 失败

    // 090-099 单文件模式
    SV090, // 单文件模式：plan 非自包含，跳过
}

public sealed record StructValueSemanticsDiagnostic(
    StructValueSemanticsSeverity Severity,
    StructValueSemanticsDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? StructName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning SV0xx: [Ns.S] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            StructValueSemanticsSeverity.Error => "error",
            StructValueSemanticsSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = StructName != null ? $"[{StructName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器（设计 §4.5）。</summary>
public sealed class StructValueSemanticsStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int StructsScanned { get; set; }
    public int StructsPlanned { get; set; }
    public int StructsReusedClone { get; set; }
    public int ShimsGenerated { get; set; }
    public int SitesRewritten { get; set; }

    public int LocalInitSites { get; set; }
    public int AssignSites { get; set; }
    public int ChainedAssignSites { get; set; }
    public int ReturnSites { get; set; }
    public int ArgumentSites { get; set; }
    public int DefensiveReceiverSites { get; set; }
    public int ArrayStoreSites { get; set; }
    public int ArrayLoadSites { get; set; }
    public int BoxingSites { get; set; }
    public int InterfaceSites { get; set; }
    public int UnboxSites { get; set; }

    /// <summary>VS-6：无初始化器的 struct 类型实例字段（仅登记供 W 系列审计，不插 clone）。</summary>
    public int UninitializedStructFields { get; set; }

    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（设计 §6：按原因分组）。</summary>
    public Dictionary<StructValueSemanticsDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class StructValueSemanticsOptions
{
    /// <summary>显式排除的完全限定类型名集合（overrides.skip；SV040 族具名逃生口）。</summary>
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>强制处理的完全限定类型名集合（overrides.force，仅解除保守插入跳过）。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在 Error 级诊断时退出码非零（默认 true：响亮失败口径）。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>是否输出 Info 级诊断。</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>是否在值参点插 clone（预留分批开关，默认全开）。</summary>
    public bool InsertAtValueArguments { get; init; } = true;

    /// <summary>是否在装箱点插 clone（预留分批开关，默认全开）。</summary>
    public bool InsertAtBoxingSites { get; init; } = true;
}

public sealed record StructValueSemanticsFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
