using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>Caller 四族（design §4.3 AN-C）。</summary>
public enum CallerInfoKind
{
    CallerMemberName,
    CallerFilePath,
    CallerLineNumber,
    CallerArgumentExpression,
}

/// <summary>plan 种类（design §2）。</summary>
public enum CallerPlanKind
{
    /// <summary>目标方法声明在源内：填站点 + 去声明属性。</summary>
    SourceDeclaration,

    /// <summary>目标方法在外部程序集：只填站点，无声明可改。</summary>
    ExternalTarget,
}

/// <summary>plan 结果（design §4.1：Active/Skipped）。</summary>
public enum CallerPlanOutcome
{
    Active,
    Skipped,
}

/// <summary>
/// 一个调用点省略信息（语法节点 + 形参索引 + 计算出的常量值）。
/// </summary>
public sealed class CallerSite
{
    /// <summary>调用语法节点（InvocationExpression/ObjectCreationExpression/
    /// ConstructorInitializer/ElementAccess）。</summary>
    public required SyntaxNode CallNode { get; init; }

    /// <summary>站点位置（调用节点位置）。</summary>
    public required Location SiteLocation { get; init; }

    /// <summary>目标形参符号。</summary>
    public required IParameterSymbol Parameter { get; init; }

    /// <summary>目标形参序号。</summary>
    public required int ParameterOrdinal { get; init; }

    /// <summary>Caller 族。</summary>
    public required CallerInfoKind Kind { get; init; }

    /// <summary>字符串常量值（MemberName/FilePath/ArgumentExpression 用）。</summary>
    public string? StringValue { get; set; }

    /// <summary>int 常量值（LineNumber 用）。</summary>
    public int? IntValue { get; set; }

    /// <summary>不可行站点的失败码（CS2J1302/1303/1304）。</summary>
    public string? FailCode { get; set; }

    /// <summary>不可行原因。</summary>
    public string? FailReason { get; set; }

    public bool IsFeasible => FailCode == null;

    public string FilePath => SiteLocation.SourceTree?.FilePath ?? string.Empty;
}

/// <summary>
/// Caller 归一 plan，回退最小单位（design §4.1）。
/// 键确定性：<c>caller:&lt;目标方法元数据名&gt;(&lt;参数索引集合&gt;)</c>。
/// </summary>
public sealed class CallerInfoPlan
{
    /// <summary>确定性 plan 键。</summary>
    public required string Id { get; init; }

    /// <summary>被调方法符号（索引器为 get_X/set_X 访问器方法）。</summary>
    public required IMethodSymbol TargetMethod { get; init; }

    /// <summary>SourceDeclaration / ExternalTarget。</summary>
    public required CallerPlanKind Kind { get; init; }

    /// <summary>全部省略站点（含不可行站点），按文件+span 确定性排序。</summary>
    public List<CallerSite> Sites { get; } = new();

    /// <summary>源内目标声明位置（ExternalTarget 为 null）。</summary>
    public Location? DeclarationLocation { get; set; }

    /// <summary>Active / Skipped。</summary>
    public CallerPlanOutcome Outcome { get; set; } = CallerPlanOutcome.Active;

    /// <summary>跳过时的失败码。</summary>
    public string? FailCode { get; set; }

    /// <summary>跳过综合原因（消息分列站点）。</summary>
    public string? FailReason { get; set; }

    /// <summary>源内目标声明端 Caller attribute 的语法位置（residual 对账登记）。</summary>
    public List<Location> DeclarationCallerAttributeLocations { get; } = new();

    /// <summary>该 plan 触碰文件集合（源内=声明文件+站点文件；外部=站点文件）。</summary>
    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 该 plan 是否拥有 (file, span) 触碰：站点或声明位置与 span 相交即命中。
    /// </summary>
    public bool OwnsTouch(string file, TextSpan span)
    {
        if (!TouchedFiles.Contains(file)) return false;

        foreach (var site in Sites)
        {
            if (!string.Equals(site.FilePath, file, StringComparison.OrdinalIgnoreCase))
                continue;
            if (site.SiteLocation.SourceSpan.IntersectsWith(span)) return true;
        }

        if (DeclarationLocation is { } decl
            && string.Equals(decl.SourceTree?.FilePath, file, StringComparison.OrdinalIgnoreCase)
            && decl.SourceSpan.IntersectsWith(span))
        {
            return true;
        }

        return false;
    }
}
