using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EnumDeclaration;

internal enum EnumPlanOutcome
{
    /// <summary>命中 D2 且全部使用点可表示：重写为 enum-like sealed class。</summary>
    Converted,

    /// <summary>ED010 常量缺失 / ED020 不可表示 / ED011 符号不全 / ED090 跨文件：保持真 enum。</summary>
    Infeasible,
}

/// <summary>按需生成的可选成员（D6）。</summary>
[Flags]
internal enum EnumOptionalMembers
{
    None = 0,
    HasFlag = 1 << 0,
    ToStringFormat = 1 << 1,
    Format = 1 << 2,
    GetValues = 1 << 3,
    GetNames = 1 << 4,
    GetName = 1 << 5,
    IsDefinedValue = 1 << 6,
    IsDefinedName = 1 << 7,
    Parse = 1 << 8,
    TryParse = 1 << 9,
    ToObject = 1 << 10,
    CompareTo = 1 << 11,
}

internal sealed record EnumMemberInfo(
    IFieldSymbol Symbol,
    string Name,
    object? ConstantValue,
    ulong Bits,
    bool HasEqualsValue,
    Location Location);

/// <summary>不可表示站点（ED020）：整 plan Infeasible，逐条列出位置。</summary>
internal sealed record EnumInfeasibleSite(Location Location, string Reason);

/// <summary>名字契约站点（ED040，Warning 不阻断）。</summary>
internal sealed record EnumNameContractSite(Location Location, string Message);

internal sealed record EnumPlanTouch(string File, string? TypeName, string? MemberName);

/// <summary>D16 碰撞消解后的合成成员内部名（Rewriter 改写用点时必须使用这些名）。</summary>
internal sealed class EnumSynthesizedNames
{
    public required string BackingField { get; init; }
    public required string GetValue { get; init; }
    public required string FromValue { get; init; }
    public required string DefaultInstance { get; init; }
    public required string HexHelper { get; init; }
    public required string ParseTokenHelper { get; init; }
    public required string TryParseTokenHelper { get; init; }

    /// <summary>按需可选成员（D6）的最终方法名（可能经 D16 加序号）。</summary>
    public Dictionary<EnumOptionalMembers, string> MethodNames { get; } = new();

    public string Method(EnumOptionalMembers kind, string fallback)
        => MethodNames.TryGetValue(kind, out var name) ? name : fallback;
}

internal sealed class EnumDeclarationPlan
{
    /// <summary>完全限定名（D15）。</summary>
    public required string Id { get; init; }
    public required string EnumShortName { get; init; }
    public required string EnumQualifiedName { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }
    public required EnumDeclarationSyntax Declaration { get; init; }

    /// <summary>底层整数类型（D3 八类）。</summary>
    public required SpecialType Underlying { get; init; }
    public required INamedTypeSymbol UnderlyingSymbol { get; init; }

    public bool IsFlags { get; set; }
    public bool IsNonInt { get; set; }
    public bool HasExplicitValue { get; set; }
    public bool HasDuplicateValue { get; set; }

    public List<EnumMemberInfo> Members { get; } = new();
    public EnumOptionalMembers Demanded { get; set; } = EnumOptionalMembers.None;

    /// <summary>Synthesizer 物化的碰撞消解命名（M2 后非空）。</summary>
    public EnumSynthesizedNames? Names { get; set; }

    public EnumPlanOutcome Outcome { get; set; } = EnumPlanOutcome.Converted;
    public EnumDeclarationDiagnosticCode? FailCode { get; set; }
    public string? FailReason { get; set; }
    public Location? FailLocation { get; set; }
    public bool Feasible => Outcome == EnumPlanOutcome.Converted;

    public List<EnumInfeasibleSite> InfeasibleSites { get; } = new();
    public List<EnumNameContractSite> NameContractSites { get; } = new();

    /// <summary>ED012 跨形态强转站点计数（§2.2）。</summary>
    public int CrossFormCastCount { get; set; }

    /// <summary>ED013 E04 残余口径站点计数。</summary>
    public int ResidualE04Count { get; set; }

    public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EnumPlanTouch> Touches { get; } = new();

    public bool Reported { get; set; }

    public void Demand(EnumOptionalMembers member) => Demanded |= member;

    public bool Needs(EnumOptionalMembers member) => (Demanded & member) != 0;

    public string DeclarationFile => Symbol.Locations
        .FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? string.Empty;

    public bool OwnsTouch(string file, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(file)) return false;
        if (typeName == null) return true;
        if (typeName == EnumShortName) return true;
        return memberName != null
            && Touches.Any(t => string.Equals(t.MemberName, memberName, StringComparison.Ordinal));
    }

    public (string? File, int Line, int Column) PrimaryLocation()
    {
        var loc = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan().StartLinePosition;
        return (loc?.SourceTree?.FilePath, (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1);
    }
}

internal sealed class EnumAnalysis
{
    public Compilation Compilation { get; init; } = null!;

    /// <summary>编译内全部源内 enum 的计划（含 Infeasible），按完全限定名排序。</summary>
    public List<EnumDeclarationPlan> Plans { get; } = new();

    /// <summary>ED010：成员常量无法求值、plan 不建立的枚举诊断。</summary>
    public List<EnumDeclarationDiagnostic> ConstantDiagnostics { get; } = new();

    public Dictionary<INamedTypeSymbol, EnumDeclarationPlan> BySymbol { get; }
        = new(SymbolEqualityComparer.Default);
}

internal static class EnumPlanBuilder2
{
    /// <summary>D2 命中判定：①[Flags] ②U 非 int ③任一成员 EqualsValue ④成员值重复。</summary>
    public static bool IsProblem(
        bool isFlags,
        SpecialType underlying,
        bool anyExplicitValue,
        bool anyDuplicateValue)
        => isFlags
            || underlying != SpecialType.System_Int32
            || anyExplicitValue
            || anyDuplicateValue;
}
