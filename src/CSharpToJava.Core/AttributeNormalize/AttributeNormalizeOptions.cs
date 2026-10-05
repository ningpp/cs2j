namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// CallerFilePath 取值策略（design §2）：
/// FreezeAnalyzed=冻结 C10 输入分析树 SyntaxTree.FilePath；
/// Empty=插入 ""；Error=按 CS2J1304 跳过 plan。
/// </summary>
public enum CallerFilePathPolicy
{
    FreezeAnalyzed,
    Empty,
    Error,
}

/// <summary>
/// attribute-normalize pass 选项（design §4.1）。
/// </summary>
public sealed record AttributeNormalizeOptions
{
    /// <summary>残余/未知项 Warning 升级 Error（CLI --strict）。</summary>
    public bool Strict { get; init; }

    /// <summary>是否输出 Info 级诊断（AN001/AN002/AN003）。</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>CallerFilePath 取值策略，默认 FreezeAnalyzed。</summary>
    public CallerFilePathPolicy CallerFilePathPolicy { get; init; } = CallerFilePathPolicy.FreezeAnalyzed;

    /// <summary>增补白名单：属性完全限定元数据名（不报 CS2J1320）。</summary>
    public IReadOnlySet<string> KeepAttributes { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>源码生成器/编译期-only 属性：不盘点不告警。</summary>
    public IReadOnlySet<string> IgnoreAttributes { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>类型完全限定名：整体不分析、不改写。</summary>
    public IReadOnlySet<string> SkipTypes { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);
}
