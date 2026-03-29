using CSharpToJava.Core.Pipeline.Compatibility;

namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// 从转换结果中提取 compatibility pack 需求，并映射为构建规划可消费的数据。
/// </summary>
public static class CompatibilityPackPlanner
{
    public static CompatibilityPackRequirements Analyze(
        IReadOnlyList<ConversionResult> convertedResults,
        string targetPackage,
        CompatibilityPackRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(convertedResults);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPackage);

        registry ??= CompatibilityPackRegistry.CreateDefault();
        var applicablePacks = registry.GetApplicablePacks(convertedResults, targetPackage);

        var packIds = applicablePacks
            .Select(pack => pack.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var externalDependencies = WorkspacePlanBuilder.MergeDependencies(
            applicablePacks
                .SelectMany(pack => pack.MavenDependencies)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(coordinate => WorkspacePlanBuilder.ExternalDependency(coordinate)));

        return new CompatibilityPackRequirements
        {
            RequiredPackIds = packIds,
            ExternalDependencies = externalDependencies,
        };
    }
}

public sealed class CompatibilityPackRequirements
{
    public required IReadOnlyList<string> RequiredPackIds { get; init; }
    public required IReadOnlyList<JavaDependency> ExternalDependencies { get; init; }
}