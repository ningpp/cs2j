namespace CSharpToJava.Core.EnumDeclaration;

public enum EnumDeclarationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>ED0xx 诊断码（设计 §6）。码位只追加，不改号。</summary>
public enum EnumDeclarationDiagnosticCode
{
    // 001-009 转换结果（Info）
    ED001, // 枚举已归一（flags/non-int/explicit/dup，U）
    ED002, // 使用点改写分类计数

    // 010-019 分析/提示
    ED010, // 成员常量值无法求值（D4），plan 不建立
    ED011, // IErrorTypeSymbol，符号信息不全，plan 跳过
    ED012, // 跨形态强转站点（被改写 enum ↔ 保留普通 enum）
    ED013, // E04 残余口径站点透传计数

    // 020-029 不可表示（plan Infeasible）
    ED020,

    // 030-039 整树验证/回滚
    ED030, // 新增错误无法归属单 plan → 整轮回退
    ED031, // 新增错误唯一归属某 plan → 该 plan 原子回退

    // 040-049 契约/restore
    ED040, // 名字契约/序列化站点（D10）
    ED041, // dotnet restore 失败

    // 090-099 单文件模式
    ED090, // 单文件模式 plan 跨文件，跳过
}

public sealed record EnumDeclarationDiagnostic(
    EnumDeclarationSeverity Severity,
    EnumDeclarationDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? EnumName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning ED0xx: [Enum] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            EnumDeclarationSeverity.Error => "error",
            EnumDeclarationSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = EnumName != null ? $"[{EnumName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器。</summary>
public sealed class EnumDeclarationStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    public int EnumsScanned { get; set; }
    public int EnumsRewritten { get; set; }
    public int FlagsCount { get; set; }
    public int NonIntCount { get; set; }
    public int ExplicitCount { get; set; }
    public int DuplicateCount { get; set; }

    // 使用点分类计数
    public int MemberRefSites { get; set; }
    public int CastSites { get; set; }
    public int SwitchStatementSites { get; set; }
    public int SwitchExpressionSites { get; set; }
    public int PatternSites { get; set; }
    public int EnumApiSites { get; set; }
    public int FormatSites { get; set; }
    public int CompareSites { get; set; }
    public int DefaultSites { get; set; }
    public int NullableSites { get; set; }
    public int ConstDowngradeSites { get; set; }
    public int UseSitesRewritten => CastSites + SwitchStatementSites + SwitchExpressionSites
        + PatternSites + EnumApiSites + FormatSites + CompareSites + DefaultSites
        + NullableSites + ConstDowngradeSites;

    public int PlansSkipped { get; set; }
    public int PlansRolledBack { get; set; }
    public int ResidualCount { get; set; }

    /// <summary>按诊断码分桶的跳过数（设计 §6）。</summary>
    public Dictionary<EnumDeclarationDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class EnumDeclarationOptions
{
    /// <summary>ED012/ED013/ED040 在 strict 下升级为 Error（设计 §6）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否输出 Info 级诊断（ED001/ED002）。</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>单文件模式：跨文件 plan 判 ED090 跳过（项目适配器始终整树，传 false）。</summary>
    public bool SingleFileMode { get; init; }
}

public sealed record EnumDeclarationFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
