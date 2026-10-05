namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// DS0xx 诊断码（设计 §6 诊断策略表，只追加不改号）。M1 发射 DS002/DS010-DS019/DS070；
/// DS001/DS030/DS040/DS050-DS052/DS060/DS061 为 M2 预留，仅定义不发射。
/// </summary>
public static class DynamicDesugarDiagnosticCodes
{
    /// <summary>P1 声明已具体化（消息含声明位置、dynamic → T）。Info（M2 rewrite）。</summary>
    public const string ProvenConcretized = "DS001";

    /// <summary>P2 站点已由用户显式转换（(T)d），本就静态绑定。恒 Info。</summary>
    public const string ExplicitCastP2 = "DS002";

    /// <summary>dynamic 局部/变量不可证（消息必带位置与建议动作，M2 起带 UnprovableReason）。</summary>
    public const string LocalUnprovable = "DS010";

    /// <summary>dynamic 方法参数或返回类型（跨边界签名，M2 不改；M3 私方法单态候选项）。</summary>
    public const string CrossBoundarySignature = "DS011";

    /// <summary>dynamic 字段/属性/事件/索引器存储（永不自动重写）。</summary>
    public const string FieldLikeStorage = "DS012";

    /// <summary>dynamic 调用/成员访问（CandidateReason.LateBound），消息含表达式与来源类型。</summary>
    public const string DynamicMemberOperation = "DS013";

    /// <summary>dynamic 元素访问（ElementAccess 静默 .get 兜底面对的站点）。</summary>
    public const string DynamicElementAccess = "DS014";

    /// <summary>dynamic 运算（二元/一元/复合赋值/自增自减/dynamic await）。</summary>
    public const string DynamicOperation = "DS015";

    /// <summary>T→dynamic 隐式边界（动态实参/赋值），消息记录来源类型 T。</summary>
    public const string DynamicConversion = "DS016";

    /// <summary>ExpandoObject/DynamicObject/JSON dynamic（JObject/JToken/DeserializeObject&lt;dynamic&gt;）；消息附手工配方。</summary>
    public const string ExpandoJsonInterop = "DS017";

    /// <summary>Microsoft.CSharp.RuntimeBinder.* 显式引用（RuntimeBinderException 语义无 Java 对应，D8）。</summary>
    public const string RuntimeBinderApi = "DS018";

    /// <summary>元数据 object + [Dynamic] 入口站点（外部程序集 dynamic 成员，D12）。</summary>
    public const string DynamicAttributeEntry = "DS019";

    /// <summary>residual 闸门违规：输出树仍有非登记 dynamic 站点。Info（audit 不闸门）/Error。</summary>
    public const string ResidualGateViolation = "DS030";

    /// <summary>改写后三候选语法校验全失败，原文保留（M2，对齐 E06/JK invalid-syntax 保护）。</summary>
    public const string RewriteSyntaxInvalid = "DS040";

    /// <summary>中间树 dotnet restore 失败。Warning。</summary>
    public const string RestoreFailed = "DS050";

    /// <summary>中间树 build 失败。Error。</summary>
    public const string BuildFailed = "DS051";

    /// <summary>中间树 test 失败。Error。</summary>
    public const string TestsFailed = "DS052";

    /// <summary>残余站点命中 manifest 条目（显式知情残留；--strict 升 Error）。</summary>
    public const string ManifestSuppressed = "DS060";

    /// <summary>manifest 陈旧条目（源内已无对应站点），强制登记维护。Warning。</summary>
    public const string StaleManifestEntry = "DS061";

    /// <summary>报告产物写出（路径、站点数）。Info。</summary>
    public const string ReportWritten = "DS070";
}

/// <summary>诊断分级（设计 §6）。</summary>
public enum DynamicDesugarSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// dynamic-desugar 通道自身诊断（DS0xx）。FilePath/Line/Column 在构造时即为
/// 1 基人类可读坐标（记录内部存 0 基，跨边界处 +1）。
/// </summary>
public sealed record DynamicDesugarDiagnostic(
    string Code,
    DynamicDesugarSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild 格式：<c>file(line,col): warning DS0xx: message</c>（仿 JK/RR）。</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            DynamicDesugarSeverity.Error => "error",
            DynamicDesugarSeverity.Warning => "warning",
            _ => "info",
        };
        var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{location}{level} {Code}: {Message}";
    }

    /// <summary>DS070 报告写出诊断（供适配器落盘后发射）。</summary>
    public static DynamicDesugarDiagnostic CreateReportWritten(string reportPath, int siteCount)
        => new(
            DynamicDesugarDiagnosticCodes.ReportWritten,
            DynamicDesugarSeverity.Info,
            $"dynamic-desugar audit report written to '{reportPath}': {siteCount} site(s).");
}

/// <summary>
/// 码位 → 分级策略（设计 §3 D3 / §6 表）。audit 默认 Warning/Info；rewrite 默认 Error；
/// strict 把 Warning 升 Error；DS002 恒 Info。返回 null 表示该站点不发射诊断
/// （基线错误树上的 UnresolvedSymbol 保守记录不叠加噪声，设计 §4.3/R10）。
/// </summary>
internal static class DynamicDesugarSeverityPolicy
{
    public static DynamicDesugarSeverity? Resolve(DynamicSiteRecord site, DynamicDesugarOptions options)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(options);

        // error 类型节点的保守台账不重复编译器错误噪声（仅入清单/报告）。
        if (site.UnprovableReason == DynamicUnprovableReason.UnresolvedSymbol
            && !site.SymbolUnresolved)
        {
            return null;
        }

        switch (site.Code)
        {
            case DynamicDesugarDiagnosticCodes.ProvenConcretized:
            case DynamicDesugarDiagnosticCodes.ExplicitCastP2:
            case DynamicDesugarDiagnosticCodes.ReportWritten:
                return DynamicDesugarSeverity.Info;

            case DynamicDesugarDiagnosticCodes.LocalUnprovable:
            case DynamicDesugarDiagnosticCodes.CrossBoundarySignature:
            case DynamicDesugarDiagnosticCodes.FieldLikeStorage:
            case DynamicDesugarDiagnosticCodes.DynamicMemberOperation:
            case DynamicDesugarDiagnosticCodes.DynamicElementAccess:
            case DynamicDesugarDiagnosticCodes.DynamicOperation:
            case DynamicDesugarDiagnosticCodes.DynamicConversion:
            case DynamicDesugarDiagnosticCodes.ExpandoJsonInterop:
            case DynamicDesugarDiagnosticCodes.RuntimeBinderApi:
            case DynamicDesugarDiagnosticCodes.DynamicAttributeEntry:
                if (options.Mode == DynamicMode.Rewrite)
                {
                    return DynamicDesugarSeverity.Error;
                }

                return options.Strict
                    ? DynamicDesugarSeverity.Error
                    : DynamicDesugarSeverity.Warning;

            case DynamicDesugarDiagnosticCodes.ManifestSuppressed:
                return options.Strict
                    ? DynamicDesugarSeverity.Error
                    : (options.Mode == DynamicMode.Audit
                        ? DynamicDesugarSeverity.Info
                        : DynamicDesugarSeverity.Warning);

            case DynamicDesugarDiagnosticCodes.ResidualGateViolation:
            case DynamicDesugarDiagnosticCodes.RewriteSyntaxInvalid:
            case DynamicDesugarDiagnosticCodes.BuildFailed:
            case DynamicDesugarDiagnosticCodes.TestsFailed:
                return DynamicDesugarSeverity.Error;

            case DynamicDesugarDiagnosticCodes.RestoreFailed:
            case DynamicDesugarDiagnosticCodes.StaleManifestEntry:
                return options.Strict
                    ? DynamicDesugarSeverity.Error
                    : DynamicDesugarSeverity.Warning;

            default:
                return options.Strict
                    ? DynamicDesugarSeverity.Error
                    : DynamicDesugarSeverity.Warning;
        }
    }
}
