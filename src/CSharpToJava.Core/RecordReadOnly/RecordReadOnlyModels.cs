namespace CSharpToJava.Core.RecordReadOnly;

public enum RecordReadOnlySeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>RR0xx 诊断码。码位分段见各枚举项注释；新增码只能追加，不能改号。</summary>
public enum RecordReadOnlyDiagnosticCode
{
    // 001-009 转换结果（Info）
    RR001, // record struct 已转换
    RR002, // record class 已转换
    RR010, // 已是 readonly record struct，跳过（RS-18）
    RR011, // overrides.skip 显式排除（FB-4）
    RR012, // overrides.force 强制转换（FB-4/FB-3）
    RR013, // record class 别名观察语义变化提示（§4.2，Info）
    RR014, // 私有拷贝 ctor 限制提示（RC-10，Info）

    // 020-029 计划级跳过/回退
    RR020, // 单 record 原子跳过（FB-1，原因在 Message）
    RR021, // record class 继承层次整体跳过（FB-2）
    RR030, // 整树验证失败且无法归属单计划 → 本次运行全部回退（FB-5）
    RR031, // 整树验证新增错误归属某 record 计划 → 回退该计划（FB-5）

    // 040-049 声明级不可改写
    RR040, // 实例事件（RS-10）
    RR041, // volatile/ref-return/fixed/unsafe 取址（RS-11；RC-5 对外可见 ref 返回）
    RR042, // 带 set 的索引器（RS-19/RC-9/MS-18）
    RR043, // StructLayout.Explicit 且字段需转属性（RS-17）

    // 050-059 用点不可改写
    RR050, // 接收者不可写（MS-1：in/readonly/foreach/get-only/方法返回值；MS-13/MS-16）
    RR051, // 接收者表达式有副作用（MS-1/MS-5）
    RR052, // 复合/自增但 getter 不纯（MS-3/MS-4）
    RR053, // 表达式位置的变异（MS-4/MS-6/MS-16）
    RR054, // 只读集合/只读索引器元素（MS-7）
    RR055, // record 以 ref/out 传入外部方法（MS-10）
    RR056, // 对 record 字段取 ref/out 传外部（MS-11）
    RR057, // dynamic/反射修改（MS-14）
    RR058, // 方法组绑定被迁移签名方法（MS-15）

    // 060-069 方法迁移不可行
    RR060, // 迭代器/async mutating 方法（MM-6）
    RR061, // partial 方法（MM-6）
    RR062, // 方法调用图含环（MM-1/MM-6）
    RR063, // getter 访问器体内修改其他成员（MM-6）
    RR064, // this = expr 无法展开：含 with 不可设置的实例状态（MM-3/MM-6）

    // 070-079 接口连带
    RR070, // 外部接口无法加成员（IR-7/FB-3）
    RR071, // With 成员签名碰撞（IR-7）

    // 080-089 record class 专项
    RR080, // 私有/不可访问拷贝 ctor，外部 with 不可用（RC-10）

    // 090-099 单文件模式
    RR090, // 单文件模式：record 非自包含，跳过（§3.3）
}

public sealed record RecordReadOnlyDiagnostic(
    RecordReadOnlySeverity Severity,
    RecordReadOnlyDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? RecordName = null)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning RR0xx: [Record] message</c>。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            RecordReadOnlySeverity.Error => "error",
            RecordReadOnlySeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = RecordName != null ? $"[{RecordName}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}

/// <summary>CLI 汇总行与断言使用的计数器。</summary>
public sealed class RecordReadOnlyStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int RecordsScanned { get; set; }
    public int RecordStructsConverted { get; set; }
    public int RecordClassesConverted { get; set; }
    public int AlreadyReadOnly { get; set; }
    public int RecordsSkipped { get; set; }
    public int MembersSetToInit { get; set; }
    public int FieldsToProperties { get; set; }
    public int MutationSitesRewritten { get; set; }
    public int MethodsMigrated { get; set; }
    public int InterfaceRipples { get; set; }
    public int PlansRolledBack { get; set; }

    /// <summary>按诊断码分桶的跳过数（规范 §6：按原因分组）。</summary>
    public Dictionary<RecordReadOnlyDiagnosticCode, int> SkippedByReason { get; }
        = new();
}

public sealed class RecordReadOnlyOptions
{
    /// <summary>强制转换的完全限定类型名集合（命中则尽量降级为显式桩，FB-3/FB-4）。</summary>
    public IReadOnlySet<string> ForceQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>显式排除的完全限定类型名集合（FB-4）。</summary>
    public IReadOnlySet<string> SkipQualifiedNames { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>存在跳过时退出码非零（规范 §6，CLI --strict；默认 false）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否转换 record class（RC 规则）。M5 任务落地前保持 false；M5 翻为 true。</summary>
    public bool ConvertRecordClasses { get; init; } = false;

    /// <summary>是否输出 Info 级诊断。</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record RecordReadOnlyFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
