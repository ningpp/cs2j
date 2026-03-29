using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline.Planning;

public sealed class CanaryDiagnosticSeveritySummary
{
    public required int InfoCount { get; init; }
    public required int WarningCount { get; init; }
    public required int ErrorCount { get; init; }
}

public sealed class CanaryDiagnosticCategoryEntry
{
    public required string Category { get; init; }
    public required int Count { get; init; }
}

public sealed class CanaryDiagnosticCodeEntry
{
    public required string Code { get; init; }
    public required int Count { get; init; }
}

public sealed class CanarySummarySnapshot
{
    public required string SourceName { get; init; }
    public string? SourcePath { get; init; }
    public required int ModuleCount { get; init; }
    public required int ResultCount { get; init; }
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
    public required CanaryDiagnosticSeveritySummary DiagnosticSeverities { get; init; }
    public required IReadOnlyList<CanaryDiagnosticCategoryEntry> DiagnosticCategories { get; init; }
    public required IReadOnlyList<CanaryDiagnosticCodeEntry> DiagnosticCodes { get; init; }
    public required IReadOnlyList<PassProfileAggregateEntry> PassAggregates { get; init; }
}

public static class CanarySummarySnapshotBuilder
{
    public static CanarySummarySnapshot Build(
        string sourceName,
        string? sourcePath,
        IReadOnlyList<ConversionResult> results,
        PassProfileSnapshot? passProfileSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(results);

        var diagnostics = results.SelectMany(result => result.Diagnostics).ToList();

        return new CanarySummarySnapshot
        {
            SourceName = sourceName,
            SourcePath = sourcePath,
            ModuleCount = DetermineModuleCount(passProfileSnapshot),
            ResultCount = results.Count,
            SuccessCount = results.Count(result => result.Success),
            FailureCount = results.Count(result => !result.Success),
            DiagnosticSeverities = new CanaryDiagnosticSeveritySummary
            {
                InfoCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info),
                WarningCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
                ErrorCount = diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            },
            DiagnosticCategories = diagnostics
                .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Category))
                .GroupBy(diagnostic => diagnostic.Category!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CanaryDiagnosticCategoryEntry
                {
                    Category = group.Key,
                    Count = group.Count(),
                })
                .ToList(),
            DiagnosticCodes = diagnostics
                .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Code))
                .GroupBy(diagnostic => diagnostic.Code!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CanaryDiagnosticCodeEntry
                {
                    Code = group.Key,
                    Count = group.Count(),
                })
                .ToList(),
            PassAggregates = passProfileSnapshot?.Aggregates.ToList() ?? [],
        };
    }

    private static int DetermineModuleCount(PassProfileSnapshot? passProfileSnapshot)
    {
        var moduleCount = passProfileSnapshot?.Entries
            .Select(entry => entry.ModuleName)
            .Where(moduleName => !string.IsNullOrWhiteSpace(moduleName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() ?? 0;

        return moduleCount == 0 ? 1 : moduleCount;
    }
}

public sealed class CanarySummarySnapshotJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public string Serialize(CanarySummarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}