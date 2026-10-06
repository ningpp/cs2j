namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>诊断级别。</summary>
public enum AttributeNormalizeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 诊断码常量（design §6）。CS2J13xx 段与 AN00x Info 码；
/// 码位只追加不改号。
/// </summary>
public static class AttributeNormalizeDiagnosticCode
{
    /// <summary>整树重编译新增错误且无法归属单一 plan → 本次运行全部回退。</summary>
    public const string CS2J1300 = "CS2J1300";

    /// <summary>Caller plan 原子跳过（含不可行站点综合原因；消息分列站点）。</summary>
    public const string CS2J1301 = "CS2J1301";

    /// <summary>dynamic/不可绑定调用点。</summary>
    public const string CS2J1302 = "CS2J1302";

    /// <summary>调用点位于表达式树 lambda 内。</summary>
    public const string CS2J1303 = "CS2J1303";

    /// <summary>Caller 值上下文无法静态确定。</summary>
    public const string CS2J1304 = "CS2J1304";

    /// <summary>序列化注解桥接面缺口。</summary>
    public const string CS2J1310 = "CS2J1310";

    /// <summary>白名单外自定义 attribute 使用点。</summary>
    public const string CS2J1320 = "CS2J1320";

    /// <summary>命中已知无桥 denylist attribute。</summary>
    public const string CS2J1321 = "CS2J1321";

    /// <summary>residual 闸门：残留无 skip 登记的 Caller attribute。</summary>
    public const string CS2J1322 = "CS2J1322";

    /// <summary>中间树 dotnet restore/build 失败。</summary>
    public const string CS2J1330 = "CS2J1330";

    /// <summary>中间树 dotnet test 失败。</summary>
    public const string CS2J1331 = "CS2J1331";

    /// <summary>单文件模式：plan 非自包含。</summary>
    public const string CS2J1390 = "CS2J1390";

    /// <summary>Caller plan 已应用（Info，仅 verbose）。</summary>
    public const string AN001 = "AN001";

    /// <summary>attribute inventory 汇总（Info，仅 verbose）。</summary>
    public const string AN002 = "AN002";

    /// <summary>平台/互操作 attribute 已盘点（Info，仅 verbose）。</summary>
    public const string AN003 = "AN003";

    /// <summary>
    /// attribute 类型在预处理编译中无法绑定（引用元数据缺失，常见于
    /// NuGet 包属性），短名别名也未唯一命中分类表：仅盘点不计 triage
    /// （Info，仅 verbose；不刷 CS2J1320、不参与 strict 升级）。
    /// </summary>
    public const string AN004 = "AN004";
}

/// <summary>
/// attribute-normalize 诊断（design §4.1）。
/// </summary>
public sealed record AttributeNormalizeDiagnostic(
    AttributeNormalizeSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? PlanId = null)
{
    /// <summary>
    /// MSBuild 格式：<c>file(line,col): warning/error CS2J13xx: [plan] message</c>。
    /// </summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            AttributeNormalizeSeverity.Error => "error",
            AttributeNormalizeSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        var who = PlanId != null ? $"[{PlanId}] " : string.Empty;
        return $"{loc}{level} {Code}: {who}{Message}";
    }
}
