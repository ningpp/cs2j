using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.StaticAbstractCompanion;

/// <summary>
/// Severity used by the companion lowering diagnostics. They are independent of
/// <see cref="ExpressionDesugarSeverity"/> so the plan builder can run before a
/// semantic model exists for the rewritten tree.
/// </summary>
public enum StaticAbstractCompanionSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A single companion-lowering diagnostic. Codes use the reserved C52 range
/// (CS2J0590-0599) because the C36 guard uses CS2J0585-0589 for the same
/// "Java has no equivalent" family.
/// </summary>
public sealed record StaticAbstractCompanionDiagnostic(
    StaticAbstractCompanionSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null);

/// <summary>Diagnostic codes owned by the companion lowering.</summary>
public static class StaticAbstractCompanionCodes
{
    /// <summary>An interface with static abstract/virtual members was lowered.</summary>
    public const string InterfaceLowered = "CS2J0590";

    /// <summary>An interface was left untouched because lowering is unsafe.</summary>
    public const string InterfaceSkipped = "CS2J0591";

    /// <summary>A call site shape cannot be rewritten.</summary>
    public const string CallSiteSkipped = "CS2J0592";

    /// <summary>Per-run inventory line (verbose only).</summary>
    public const string Inventory = "CS2J0593";
}

/// <summary>Accumulating counters for the companion lowering.</summary>
public sealed class StaticAbstractCompanionStatistics
{
    public int InterfacesFound;
    public int InterfacesLowered;
    public int InterfacesSkipped;
    public int Implementors;
    public int Methods;
    public int Properties;
    public int Events;
    public int Operators;
    public int Conversions;
    public int CallSitesRewritten;
    public int FilesChanged;

    public void MergeFrom(StaticAbstractCompanionStatistics other)
    {
        InterfacesFound += other.InterfacesFound;
        InterfacesLowered += other.InterfacesLowered;
        InterfacesSkipped += other.InterfacesSkipped;
        Implementors += other.Implementors;
        Methods += other.Methods;
        Properties += other.Properties;
        Events += other.Events;
        Operators += other.Operators;
        Conversions += other.Conversions;
        CallSitesRewritten += other.CallSitesRewritten;
        FilesChanged += other.FilesChanged;
    }

    public string FormatSummary()
        => $"{InterfacesLowered}/{InterfacesFound} iface, {Implementors} impl, " +
           $"{Methods} m/{Properties} p/{Events} e/{Operators} op/{Conversions} conv, " +
           $"{CallSitesRewritten} sites" +
           (InterfacesSkipped > 0 ? $", {InterfacesSkipped} skipped" : string.Empty);
}

/// <summary>
/// One interface selected for companion lowering. The generated declarations are
/// inserted next to the interface declaration (inside its containing type when
/// nested), so private sibling implementors and the interface's own type
/// parameters stay in scope.
/// </summary>
public sealed class StaticAbstractCompanionInterfacePlan
{
    public required INamedTypeSymbol Interface { get; init; }

    /// <summary>Companion instance interface name, e.g. <c>__cs2j_Companion_IPemEncoder</c>.</summary>
    public required string CompanionInterfaceName { get; init; }

    /// <summary>Static dispatch helper name, e.g. <c>__cs2j_Companion_IPemEncoder_Dispatch</c>.</summary>
    public required string DispatchName { get; init; }

    /// <summary>
    /// Expression prefix used at call sites, e.g.
    /// <c>__cs2j_Companion_IPemEncoder_Dispatch</c> (nested) or
    /// <c>global::System.Security.Cryptography.Tests.__cs2j_Companion_IHashTrait_Dispatch</c>.
    /// </summary>
    public required string DispatchReference { get; init; }

    /// <summary>Number of the interface's own type parameters (dispatch appends TImpl).</summary>
    public required int InterfaceTypeParameterCount { get; init; }

    /// <summary>Original definitions of the lowered members (methods/properties/events/operators).</summary>
    public required ImmutableArray<ISymbol> LoweredMembers { get; init; }

    /// <summary>Generated companion interface + implementor classes + dispatch class.</summary>
    public required ImmutableArray<MemberDeclarationSyntax> GeneratedMembers { get; init; }

    /// <summary>The file that receives the generated declarations.</summary>
    public required string DeclaringFilePath { get; init; }
}

/// <summary>
/// Whole-tree plan. Symbol keys are interface/member <em>original definitions</em>
/// from the compilation the plan was built from; the per-file rewriter must run
/// on that same compilation's semantic model.
/// </summary>
public sealed class StaticAbstractCompanionPlan
{
    public static StaticAbstractCompanionPlan Empty { get; } = new()
    {
        ByInterface = ImmutableDictionary.Create<ISymbol, StaticAbstractCompanionInterfacePlan>(
            SymbolEqualityComparer.Default),
        MemberDefinitions = ImmutableHashSet.Create<ISymbol>(SymbolEqualityComparer.Default),
        InterfaceNames = ImmutableArray<string>.Empty,
        SiteTokens = ImmutableArray<string>.Empty,
        RequiresFullScan = false,
        Statistics = new StaticAbstractCompanionStatistics(),
        Diagnostics = ImmutableArray<StaticAbstractCompanionDiagnostic>.Empty,
    };

    public required ImmutableDictionary<ISymbol, StaticAbstractCompanionInterfacePlan> ByInterface { get; init; }

    public required ImmutableHashSet<ISymbol> MemberDefinitions { get; init; }

    /// <summary>Interface simple names, used for the cheap per-file text gate.</summary>
    public required ImmutableArray<string> InterfaceNames { get; init; }

    /// <summary>Member simple names, used for the cheap per-file text gate.</summary>
    public required ImmutableArray<string> SiteTokens { get; init; }

    /// <summary>
    /// Operator/conversion call sites carry no member-name text anchor
    /// (<c>a + b</c> never spells <c>op_Addition</c>), so a plan with those
    /// members must visit every file.
    /// </summary>
    public required bool RequiresFullScan { get; init; }

    public required StaticAbstractCompanionStatistics Statistics { get; init; }

    public required ImmutableArray<StaticAbstractCompanionDiagnostic> Diagnostics { get; init; }

    public bool IsEmpty => ByInterface.Count == 0;

    /// <summary>
    /// Cheap gate before a file pays for a semantic walk: it must mention a
    /// lowered interface or one of the lowered member names. Type-parameter
    /// receivers are arbitrary identifiers, so the member name is the only
    /// stable textual anchor.
    /// </summary>
    public bool MayContainSites(string text)
    {
        if (IsEmpty)
            return false;

        if (RequiresFullScan)
            return true;

        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var token in SiteTokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
                return true;
        }

        foreach (var name in InterfaceNames)
        {
            if (text.Contains(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>Per-file companion rewrite result.</summary>
public readonly record struct StaticAbstractCompanionFileResult(
    string Code,
    bool Changed,
    int CallSitesRewritten);
