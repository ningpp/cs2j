namespace CSharpToJava.Core.JavaKeywords;

public enum JavaKeywordSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>JK0xx 诊断码。码位分段见各枚举项注释；新增码只能追加，不能改号。</summary>
public enum JavaKeywordDiagnosticCode
{
    // 001-009 转换结果（Info）
    JK001, // 符号已改名（消息含种类、原名→新名）
    JK002, // 类型的 operator true/false 已降级为 IsTrue/IsFalse

    // 010-019 配置
    JK010, // overrides.skip 显式排除运算符 plan

    // 020-029 计划级跳过/回退
    JK020, // 运算符 plan 原子跳过（JK-8；原因与建议在消息中）
    JK030, // 整树验证失败且无法归属单 plan → 本次运行全部回退（仿 RR030）
    JK031, // 整树新增错误归属某 plan → 该 plan 原子回退（仿 RR031）

    // 040-049 基础设施
    JK040, // dotnet restore 失败

    // 090-099 单文件模式
    JK090, // 单文件模式：plan 非自包含（跨文件声明/引用/站点），跳过
}

public sealed record JavaKeywordDiagnostic(
    JavaKeywordSeverity Severity,
    JavaKeywordDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? SymbolName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning JK0xx: [Symbol] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            JavaKeywordSeverity.Error => "error",
            JavaKeywordSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = SymbolName != null ? $"[{SymbolName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器。</summary>
public sealed class JavaKeywordStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int SymbolsRenamed { get; set; }
    public int MethodsRenamed { get; set; }
    public int PropertiesRenamed { get; set; }
    public int FieldsRenamed { get; set; }
    public int EventsRenamed { get; set; }
    public int LocalsRenamed { get; set; }
    public int ParametersRenamed { get; set; }
    public int TypesRenamed { get; set; }
    public int NamespacesRenamed { get; set; }
    public int AliasesRenamed { get; set; }
    public int NameofLiteralized { get; set; }
    public int CollisionSuffixes { get; set; }
    public int OperatorTypesDowngraded { get; set; }
    public int BooleanSitesRewritten { get; set; }
    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（规范 §7：按原因分组）。</summary>
    public Dictionary<JavaKeywordDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class JavaKeywordOptions
{
    /// <summary>force：解除表达式树项的保守跳过（JK-8）。完全限定类型名集合。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>skip：按类型完全限定名排除运算符 plan。</summary>
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时退出码非零（CLI --strict；默认 false）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否输出 Info 级诊断。</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record JavaKeywordFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
