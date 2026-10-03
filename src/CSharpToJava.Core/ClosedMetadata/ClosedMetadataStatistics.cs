namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Counters for the closed-metadata pass (design §5.1). M2' skeleton: the
/// collector/registration pipeline lands in M3, so every counter stays zero.
/// </summary>
public sealed class ClosedMetadataStatistics
{
    public int ClosedTypesEnumerated;
    public int Registered;
    public int Deduplicated;
    public int RegisterFilesGenerated;
    public int ConsumerSitesLinked;
    public int DynamicSitesSkipped;
    public Dictionary<string, int> ResidualsByRule { get; } = new(StringComparer.Ordinal);

    public void Merge(ClosedMetadataStatistics source)
    {
        ClosedTypesEnumerated += source.ClosedTypesEnumerated;
        Registered += source.Registered;
        Deduplicated += source.Deduplicated;
        RegisterFilesGenerated += source.RegisterFilesGenerated;
        ConsumerSitesLinked += source.ConsumerSitesLinked;
        DynamicSitesSkipped += source.DynamicSitesSkipped;
        foreach (var (key, value) in source.ResidualsByRule)
        {
            ResidualsByRule.TryGetValue(key, out var existing);
            ResidualsByRule[key] = existing + value;
        }
    }

    public string SummaryLine()
        => $"closed-metadata: {Registered} registered " +
           $"({ClosedTypesEnumerated} enumerated, {Deduplicated} deduplicated, " +
           $"{RegisterFilesGenerated} register files, {ConsumerSitesLinked} consumer sites linked, " +
           $"{DynamicSitesSkipped} dynamic sites skipped, {ResidualsByRule.Values.Sum()} residuals)";
}
