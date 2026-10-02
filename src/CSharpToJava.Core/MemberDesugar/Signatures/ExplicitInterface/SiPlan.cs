using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Signatures.ExplicitInterface;

/// <summary>
/// C46 SI 声明侧 plan（设计 §4.3-2）：一个「显式实现符号 × 接口槽」一 plan，
/// 同名多接口体等价时可合组（<see cref="Implementations"/> 多于一）。
/// <para>跨 Compilation 稳定键：属性/索引器/事件/运算符阶段重解析后符号来自新的
/// CSharpCompilation，<c>SymbolEqualityComparer</c> 判不相等，声明与调用点一律按
/// <see cref="SiteKey"/>（含类型全名::元数据名+参数签名）字符串匹配（镜像
/// <see cref="Indexers.IndexerPlan.SiteKey"/>）。</para>
/// </summary>
internal sealed class SiPlan : MemberDesugarPlan
{
    /// <summary>本 plan 提升的显式实现符号（M2 方法面恒为方法符号）。</summary>
    public required IReadOnlyList<ISymbol> Implementations { get; init; }

    /// <summary>对应的接口槽符号，与 <see cref="Implementations"/> 一一对应。</summary>
    public required IReadOnlyList<ISymbol> Slots { get; init; }

    /// <summary>含入类型完全限定名（确定性排序/命名预留键）。</summary>
    public required string ContainingTypeFqn { get; init; }

    /// <summary>提升后的规范槽位名（接口槽名，设计决策 1）。</summary>
    public required string PromotedName { get; init; }

    /// <summary>保留的显式实现符号的稳定键（其余同组显式声明为冗余、删除，SI-3）。</summary>
    public required string RetainedKey { get; init; }

    /// <summary>显式实现成员种类（M2 恒为 <see cref="SignatureKind.Method"/>）。</summary>
    public required SignatureKind ExplicitKind { get; init; }

    /// <summary>同名多接口体等价合并（SI-3），为真时删除冗余显式声明。</summary>
    public bool Merged { get; init; }

    /// <summary>让位的类自有同槽成员（SI-2）；无碰撞为 null。</summary>
    public ISymbol? DisplacedMember { get; init; }

    /// <summary>让位成员的稳定键（调用点绑定匹配用）。</summary>
    public string? DisplacedKey { get; init; }

    /// <summary>让位成员的新名（<c>{Name}_cs2jClass</c>，预留表防撞，二撞 <c>_2</c>）。</summary>
    public string? DisplacedName { get; init; }

    /// <summary>让位成员的原名（nameof 字面量化目标，设计决策 15）。</summary>
    public string? DisplacedOriginalName { get; init; }

    /// <summary>让位成员的归属键集合：访问器方法键 + 所属属性/事件符号键（SI-5 站点按符号分类时，
    /// 原件仍绑到 <c>IPropertySymbol/IEventSymbol</c>，须据此纳入 TouchedFiles）。</summary>
    public IReadOnlyList<string> DisplacedOwnerKeys { get; init; } = Array.Empty<string>();

    /// <summary>本 plan 覆盖的全部显式实现稳定键（residual 归属判定）。</summary>
    public IReadOnlyList<string> ImplementationKeys { get; init; } = Array.Empty<string>();

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Symbol.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return memberName != null
               && (string.Equals(memberName, PromotedName, StringComparison.Ordinal)
                   || string.Equals(memberName, DisplacedOriginalName, StringComparison.Ordinal)
                   || string.Equals(memberName, DisplacedName, StringComparison.Ordinal));
    }

    public static string BuildId(ISymbol implementation, string containingTypeFqn,
        string promotedName, string parameterSignature)
    {
        var span = implementation.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{containingTypeFqn}::{promotedName}({parameterSignature})@{span}";
    }

    /// <summary>方法/成员的跨 Compilation 稳定键：类型全名::元数据名(参数类型)。
    /// 方法/索引器带参数签名；属性/事件/字段用元数据名（索引器参数即其 Parameters）。</summary>
    public static string SiteKey(ISymbol symbol)
    {
        if (symbol is IMethodSymbol m)
            return $"{m.ContainingType?.ToDisplayString()}::{m.MetadataName}" +
                   $"({string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString()))})";
        if (symbol is IPropertySymbol { IsIndexer: true } p)
            return $"{p.ContainingType?.ToDisplayString()}::{p.MetadataName}" +
                   $"({string.Join(",", p.Parameters.Select(x => x.Type.ToDisplayString()))})";
        return $"{symbol.ContainingType?.ToDisplayString()}::{symbol.MetadataName}";
    }
}