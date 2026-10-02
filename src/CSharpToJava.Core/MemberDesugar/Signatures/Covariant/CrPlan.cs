using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Signatures.Covariant;

/// <summary>协变返回处置结论（设计决策 10/11/12）：CR-1 直映保留 / CR-2 擦除冲突桥接 / CR-3 值类型或不可判跳过。</summary>
internal enum CrVerdict
{
    Retained,
    Bridged,
    Skipped,
}

/// <summary>
/// C46 CR 协变返回 plan（设计 §4.3-2/§4.4 CR-1..CR-4）：一个协变 override 符号一 plan。
/// <list type="bullet">
/// <item>CR-1：<see cref="CrVerdict.Retained"/>——只记审计，零改写（含 C27 <c>Cs2jWithClone</c> 合成对，永不桥接）。</item>
/// <item>CR-2：<see cref="CrVerdict.Bridged"/>——保留基槽签名的 <c>override</c> bridge（体 <c>return M_cs2jCovariant(...);</c>）
/// 合成 + 原体改名为 <c>M_cs2jCovariant</c> 独立方法；派生静态类型绑定点改名，基/接口分派点不动。</item>
/// <item>CR-3：<see cref="CrVerdict.Skipped"/>——值类型/不可判，CS2J5195 原子不动。</item>
/// </list>
/// 若协变 override 在编译内还有更深的派生 override，则 <see cref="RenameByKey"/> 覆盖 Bridge 点及其
/// 整条派生 sub-chain（保持 <c>virtual</c>/<c>override</c> 语义），bridge 的委托调用经虚分派进入最深改名体。
/// </summary>
internal sealed class CrPlan : MemberDesugarPlan
{
    /// <summary>协变 override 符号（CR-2 的 bridge 点）。</summary>
    public required IMethodSymbol Member { get; init; }

    public required CrVerdict Verdict { get; init; }

    public required ITypeSymbol BaseReturnType { get; init; }
    public required ITypeSymbol DerivedReturnType { get; init; }

    /// <summary>CR-2 改名后的方法名（<c>{Name}_cs2jCovariant</c>，预留表防撞）。</summary>
    public string? RenamedName { get; init; }

    /// <summary>bridge 方法名（= 原 override 名，占基槽）。</summary>
    public string? BridgeName { get; init; }

    /// <summary>bridge 返回类型文本（基槽返回类型的最小显示）。</summary>
    public string? BaseReturnTypeText { get; init; }

    /// <summary>原 override 是否 <c>sealed</c>（sealed 时 bridge 携带 sealed、改名体非虚）。</summary>
    public bool Sealed { get; init; }

    /// <summary>编译内是否存在更深层派生的 override（整组改名时顶层改名体保留 <c>virtual</c>）。</summary>
    public bool HasDescendants { get; init; }

    /// <summary>bridge 点稳定键（声明侧桥接/改名定位）。</summary>
    public string? BridgeKey { get; init; }

    /// <summary>稳定键 → 新名（bridge 点 + 全部编译内派生 sub-chain）。</summary>
    public IReadOnlyDictionary<string, string> RenameByKey { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>稳定键 → 原名（<c>nameof</c> 字面量化目标，决策 15）。</summary>
    public IReadOnlyDictionary<string, string> OriginalNameByKey { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>本 plan 改名的声明主键集合（计数去重）。</summary>
    public IReadOnlySet<string> PrimaryKeys { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>本 plan 覆盖的全部协变成员稳定键（CR-4 residual 归属判定，含被并入的 sub-chain）。</summary>
    public IReadOnlyList<string> MemberKeys { get; init; } = Array.Empty<string>();

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Member.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return memberName != null
               && (string.Equals(memberName, Member.Name, StringComparison.Ordinal)
                   || (RenamedName != null
                       && string.Equals(memberName, RenamedName, StringComparison.Ordinal)));
    }

    public static string SiteKey(ISymbol symbol) => ExplicitInterface.SiPlan.SiteKey(symbol);

    public static string BuildId(IMethodSymbol member)
    {
        var span = member.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD-cov:{member.ContainingType?.ToDisplayString()}::{member.Name}@{span}";
    }
}