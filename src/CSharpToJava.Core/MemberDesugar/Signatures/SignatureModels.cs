using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>
/// C46 显式接口实现的成员种类（设计 §4.2-1：显式实现索引按方法/属性/索引器/事件分桶）。
/// 索引器（IPropertySymbol.IsIndexer）与普通属性分开统计。
/// </summary>
public enum SignatureKind
{
    Method,
    Property,
    Indexer,
    Event,
}

/// <summary>
/// C46 Signatures 规则集选项（rules.signatures 子节，设计 §5/§12 实施口径）。
/// 族级 <see cref="MemberDesugarOptions.Strict"/> 不在此复用——本子节自持
/// <see cref="Strict"/>（默认 true），仅作用于 CS2J5191-5197/5199 的 Warning→Error。
/// <para><see cref="Enabled"/> 默认 false：族 pass 已 defaultEnabled=true，Signatures 子集
/// 须保持「不显式启用即零执行零统计零诊断零 diff」——Enabled=false 时分析器与规则完全不执行
/// （不建索引、不扫站点）。</para>
/// </summary>
public sealed class SignatureOptions
{
    /// <summary>主闸门，默认 false（零执行）。</summary>
    public bool Enabled { get; init; }

    /// <summary>Warning 升级 Error（设计 §6），默认 true。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>是否发射 Info 级 CS2J5190 审计汇总（默认 true，仅 verbose）。</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>skip 白名单（"Ns.Type" 或 "Ns.Type::Member"），命中成员不纳入审计/改写。</summary>
    public IReadOnlySet<string> SkipSignatures { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>同名双接口分歧实现体的适配类 opt-in（设计裁决 2）。v1 强制 false，承载不生效。</summary>
    public bool AllowDivergentInterfaceBodies { get; init; }

    /// <summary>是否跳过某成员（按 "Type::Member" 或 "Type" 或纯成员名匹配，Ordinal）。</summary>
    public bool IsSkipped(ISymbol member)
    {
        if (SkipSignatures.Count == 0) return false;
        var type = member.ContainingType?.ToDisplayString();
        return SkipSignatures.Contains(member.Name)
            || (type != null
                && (SkipSignatures.Contains($"{type}::{member.Name}")
                    || SkipSignatures.Contains(type)));
    }
}

/// <summary>
/// C46 审计/改写计数器（设计 §5.2 特有计数）。M1 仅写审计三项（显式实现/隐藏/协变）；
/// 改写计数（Promoted/DisplacedRenamed/HideRenamed/...）由后续里程碑追加。
/// <para>同一个类既是整树聚合结果，也作按文件审计载体
/// （<see cref="MemberDesugarAnalysisContext.SignatureAuditByFile"/>，镜像
/// <c>MetadataOperatorSitesByFile</c>：按声明文件分桶，重写期并入族统计）。</para>
/// </summary>
public sealed class SignatureStatistics
{
    // ---- 显式接口实现（按成员种类分桶）----
    public int ExplicitMembersDetected { get; set; }
    public int ExplicitMethods { get; set; }
    public int ExplicitProperties { get; set; }
    public int ExplicitIndexers { get; set; }
    public int ExplicitEvents { get; set; }

    // ---- new 隐藏（按形态分桶）----
    public int HidingDetected { get; set; }
    public int HidingNew { get; set; }         // 显式 new 令牌（非 virtual）
    public int HidingImplicit { get; set; }    // 无 new 令牌（CS0108 隐式隐藏）
    public int HidingNewSlot { get; set; }     // new virtual/abstract（另开虚槽链）
    public int HidingStatic { get; set; }      // 静态成员隐藏

    // ---- 协变返回（按处置分桶）----
    public int CovariantChains { get; set; }
    public int CovariantRetained { get; set; }         // CR-1 直映保留
    public int CovariantBridged { get; set; }          // CR-2 擦除冲突待桥接
    public int CovariantValueTypeSkipped { get; set; } // CR-3 值类型/不可判

    public bool HasAny
        => ExplicitMembersDetected != 0 || HidingDetected != 0 || CovariantChains != 0;

    /// <summary>并入族统计（<see cref="MemberDesugarStatistics"/> 的 Signature* 计数器）。</summary>
    public void ApplyTo(MemberDesugarStatistics statistics)
    {
        statistics.SignatureExplicitMembersDetected += ExplicitMembersDetected;
        statistics.SignatureExplicitMethods += ExplicitMethods;
        statistics.SignatureExplicitProperties += ExplicitProperties;
        statistics.SignatureExplicitIndexers += ExplicitIndexers;
        statistics.SignatureExplicitEvents += ExplicitEvents;
        statistics.SignatureHidingDetected += HidingDetected;
        statistics.SignatureHidingNew += HidingNew;
        statistics.SignatureHidingImplicit += HidingImplicit;
        statistics.SignatureHidingNewSlot += HidingNewSlot;
        statistics.SignatureHidingStatic += HidingStatic;
        statistics.SignatureCovariantChains += CovariantChains;
        statistics.SignatureCovariantRetained += CovariantRetained;
        statistics.SignatureCovariantBridged += CovariantBridged;
        statistics.SignatureCovariantValueTypeSkipped += CovariantValueTypeSkipped;
    }
}