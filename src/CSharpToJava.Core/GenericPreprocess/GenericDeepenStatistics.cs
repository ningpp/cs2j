using System.Linq;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Counters for the generic-deepen pass (adapter merges per-file results).</summary>
public sealed class GenericDeepenStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int TokenAnchorsInserted;
    public int AnchorsReconciledFromE10;
    public int IsAsSitesCovered;
    public int TypeParamPatternsRewritten;
    public int CatchTypeParamsRewritten;
    public int StaticMethodsRewritten;
    public int StaticFieldHoldersGenerated;
    public int StaticFieldAccessorsGenerated;
    public int StaticFieldAccessesRewritten;
    public int CallSitesUpdated;
    public int NestedPropagations;
    public int ExpressionTreeSitesSkipped;
    public int PlansSkipped;
    public int ConstructorTokenTypesPlanned;
    public int ConstructorTokenTypesRewritten;
    public int ConstructorTokenParamsInjected;
    public int ConstructorTokenSitesRewritten;
    public int ConstructorTokenCallSitesUpdated;
    public int MethodTokenMethodsPlanned;
    public int MethodTokenMethodsRewritten;
    public int MethodTokenSitesRewritten;
    public int MethodTokenCallSitesUpdated;
    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ResidualsByRule { get; } = new(StringComparer.Ordinal);

    public void Merge(GenericDeepenStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesSkippedClean += source.FilesSkippedClean;
        TokenAnchorsInserted += source.TokenAnchorsInserted;
        AnchorsReconciledFromE10 += source.AnchorsReconciledFromE10;
        IsAsSitesCovered += source.IsAsSitesCovered;
        TypeParamPatternsRewritten += source.TypeParamPatternsRewritten;
        CatchTypeParamsRewritten += source.CatchTypeParamsRewritten;
        StaticMethodsRewritten += source.StaticMethodsRewritten;
        StaticFieldHoldersGenerated += source.StaticFieldHoldersGenerated;
        StaticFieldAccessorsGenerated += source.StaticFieldAccessorsGenerated;
        StaticFieldAccessesRewritten += source.StaticFieldAccessesRewritten;
        CallSitesUpdated += source.CallSitesUpdated;
        NestedPropagations += source.NestedPropagations;
        ExpressionTreeSitesSkipped += source.ExpressionTreeSitesSkipped;
        PlansSkipped += source.PlansSkipped;
        ConstructorTokenTypesPlanned += source.ConstructorTokenTypesPlanned;
        ConstructorTokenTypesRewritten += source.ConstructorTokenTypesRewritten;
        ConstructorTokenParamsInjected += source.ConstructorTokenParamsInjected;
        ConstructorTokenSitesRewritten += source.ConstructorTokenSitesRewritten;
        ConstructorTokenCallSitesUpdated += source.ConstructorTokenCallSitesUpdated;
        MethodTokenMethodsPlanned += source.MethodTokenMethodsPlanned;
        MethodTokenMethodsRewritten += source.MethodTokenMethodsRewritten;
        MethodTokenSitesRewritten += source.MethodTokenSitesRewritten;
        MethodTokenCallSitesUpdated += source.MethodTokenCallSitesUpdated;
        MergeCounters(SkippedByReason, source.SkippedByReason);
        MergeCounters(ResidualsByRule, source.ResidualsByRule);
    }

    public string SummaryLine()
        => $"generic-deepen: {FilesTransformed} transformed, {FilesSkippedClean} clean " +
           $"({TokenAnchorsInserted} anchors, {NestedPropagations} nested propagations, " +
           $"{TypeParamPatternsRewritten} patterns, " +
           $"{CatchTypeParamsRewritten} catches, {StaticMethodsRewritten} static methods, " +
           $"{StaticFieldAccessesRewritten} field accesses, {CallSitesUpdated} call sites, " +
           $"{ExpressionTreeSitesSkipped} expression-tree skips, {PlansSkipped} plans skipped, " +
           $"{ConstructorTokenTypesRewritten} ctor-token types, " +
           $"{ConstructorTokenSitesRewritten} ctor-token sites, " +
           $"{ConstructorTokenCallSitesUpdated} ctor-token call sites, " +
           $"{MethodTokenMethodsRewritten} mtok methods, " +
           $"{MethodTokenSitesRewritten} mtok sites, " +
           $"{MethodTokenCallSitesUpdated} mtok call sites, " +
           $"{ResidualsByRule.Values.Sum()} residuals)";

    private static void MergeCounters(Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var (key, value) in source)
        {
            target.TryGetValue(key, out var existing);
            target[key] = existing + value;
        }
    }
}
