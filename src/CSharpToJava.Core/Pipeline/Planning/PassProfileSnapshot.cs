using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.Pipeline.Planning;

public enum PassProfileEntryKind
{
    File,
    Project,
}

public sealed class PassProfileEntry
{
    public PassProfileEntryKind EntryKind { get; init; } = PassProfileEntryKind.File;
    public string? ModuleName { get; init; }
    public string? FileName { get; init; }
    public string? ProjectName { get; init; }
    public bool Success { get; init; }
    public IReadOnlyList<Cs2jPassMetric> PassMetrics { get; init; } = [];
}

public sealed class PassProfileAggregateEntry
{
    public required string Name { get; init; }
    public required Cs2jPassStage Stage { get; init; }
    public required PassProfileEntryKind EntryKind { get; init; }
    public required int EntryCount { get; init; }
    public required double TotalElapsedMilliseconds { get; init; }
    public required int TotalDiagnosticDelta { get; init; }
    public required long TotalManagedMemoryDelta { get; init; }
    public required int TotalRewriteCount { get; init; }
}

public sealed class PassProfileSnapshot
{
    public required IReadOnlyList<PassProfileEntry> Entries { get; init; }
    public required IReadOnlyList<PassProfileAggregateEntry> Aggregates { get; init; }
}

public static class PassProfileEntryBuilder
{
    public static IReadOnlyList<PassProfileEntry> CreateFileEntries(
        IEnumerable<ConversionResult> results,
        string? moduleName = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results
            .Where(result => result.PassMetrics.Count > 0)
            .Select(result => new PassProfileEntry
            {
                EntryKind = PassProfileEntryKind.File,
                ModuleName = moduleName,
                FileName = result.FileName,
                Success = result.Success,
                PassMetrics = result.PassMetrics,
            })
            .ToList();
    }

    public static PassProfileEntry? CreateProjectEntry(
        IReadOnlyList<Cs2jPassMetric> passMetrics,
        IEnumerable<ConversionResult> results,
        string? projectName,
        string? moduleName = null)
    {
        ArgumentNullException.ThrowIfNull(passMetrics);
        ArgumentNullException.ThrowIfNull(results);

        if (passMetrics.Count == 0)
        {
            return null;
        }

        var resultList = results.ToList();
        return new PassProfileEntry
        {
            EntryKind = PassProfileEntryKind.Project,
            ModuleName = moduleName,
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? moduleName : projectName,
            Success = resultList.All(result => result.Success),
            PassMetrics = passMetrics.ToList(),
        };
    }
}

public static class PassProfileSnapshotBuilder
{
    public static PassProfileSnapshot Build(IReadOnlyList<PassProfileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries.ToList();

        var aggregates = entryList
            .SelectMany(entry => entry.PassMetrics.Select(metric => new { EntryKind = entry.EntryKind, Metric = metric }))
            .GroupBy(item => new { item.Metric.Name, item.Metric.Stage, item.EntryKind })
            .OrderBy(group => group.Key.Stage)
            .ThenBy(group => group.Key.EntryKind)
            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
            .Select(group => new PassProfileAggregateEntry
            {
                Name = group.Key.Name,
                Stage = group.Key.Stage,
                EntryKind = group.Key.EntryKind,
                EntryCount = group.Count(),
                TotalElapsedMilliseconds = group.Sum(item => item.Metric.Elapsed.TotalMilliseconds),
                TotalDiagnosticDelta = group.Sum(item => item.Metric.DiagnosticDelta),
                TotalManagedMemoryDelta = group.Sum(item => item.Metric.ManagedMemoryDelta),
                TotalRewriteCount = group.Sum(item => item.Metric.RewriteCount),
            })
            .ToList();

        return new PassProfileSnapshot
        {
            Entries = entryList,
            Aggregates = aggregates,
        };
    }
}

public sealed class PassProfileSnapshotJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public string Serialize(PassProfileSnapshot snapshot)
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