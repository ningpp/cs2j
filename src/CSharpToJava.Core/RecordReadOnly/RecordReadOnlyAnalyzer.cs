using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed record RewriteContext(
    SemanticModel Model,
    RecordAnalysis Analysis,
    ConversionPlan Plan,
    IReadOnlyList<RecordPlan> ActivePlans,
    IReadOnlySet<string> ExcludedPlanIds,
    RecordReadOnlyStatistics Statistics,
    List<RecordReadOnlyDiagnostic> Diagnostics,
    string FilePath);

internal sealed class RecordAnalysis
{
    public Compilation? Compilation { get; set; }
    public List<RecordInfo> Records { get; } = new();
    public Dictionary<INamedTypeSymbol, RecordInfo> BySymbol { get; }
        = new(SymbolEqualityComparer.Default);
}

internal sealed class RecordInfo;

internal sealed class RecordReadOnlyAnalyzer
{
    private readonly RecordReadOnlyOptions _options;
    public RecordReadOnlyAnalyzer(RecordReadOnlyOptions options) => _options = options;
    public RecordAnalysis Analyze(Compilation compilation) => new();

    public static void ReportSkippedRecords(
        ConversionPlan plan,
        IReadOnlySet<string> excludedPlanIds,
        string filePath,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics,
        RecordReadOnlyOptions options)
    {
        // 桩：Task 5 计划生成 RR010/RR020 后在此落到 stats/diagnostics。
    }
}
