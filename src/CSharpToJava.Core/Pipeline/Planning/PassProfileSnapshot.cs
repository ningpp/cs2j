using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.Pipeline.Planning;

public sealed class PassProfileFileEntry
{
    public string? ModuleName { get; init; }
    public string? FileName { get; init; }
    public bool Success { get; init; }
    public IReadOnlyList<Cs2jPassMetric> PassMetrics { get; init; } = [];
}

public sealed class PassProfileAggregateEntry
{
    public required string Name { get; init; }
    public required Cs2jPassStage Stage { get; init; }
    public required int FileCount { get; init; }
    public required double TotalElapsedMilliseconds { get; init; }
    public required int TotalDiagnosticDelta { get; init; }
    public required long TotalManagedMemoryDelta { get; init; }
}

public sealed class PassProfileSnapshot
{
    public required IReadOnlyList<PassProfileFileEntry> Files { get; init; }
    public required IReadOnlyList<PassProfileAggregateEntry> Aggregates { get; init; }
}

public static class PassProfileSnapshotBuilder
{
    public static PassProfileSnapshot Build(IReadOnlyList<PassProfileFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var aggregates = files
            .SelectMany(file => file.PassMetrics)
            .GroupBy(metric => new { metric.Name, metric.Stage })
            .OrderBy(group => group.Key.Stage)
            .ThenBy(group => group.Key.Name, StringComparer.Ordinal)
            .Select(group => new PassProfileAggregateEntry
            {
                Name = group.Key.Name,
                Stage = group.Key.Stage,
                FileCount = group.Count(),
                TotalElapsedMilliseconds = group.Sum(metric => metric.Elapsed.TotalMilliseconds),
                TotalDiagnosticDelta = group.Sum(metric => metric.DiagnosticDelta),
                TotalManagedMemoryDelta = group.Sum(metric => metric.ManagedMemoryDelta),
            })
            .ToList();

        return new PassProfileSnapshot
        {
            Files = files.ToList(),
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