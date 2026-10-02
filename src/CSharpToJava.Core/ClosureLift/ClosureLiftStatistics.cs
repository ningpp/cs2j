namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Mutable accumulator (mirrors LocalFunctionLiftStatistics); merged across
/// files by the adapter. Buckets follow design §4.7.
/// </summary>
public sealed class ClosureLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    public int HolderClassesGenerated;
    public int HolderInstancesGenerated;

    /// <summary>Total captured symbols lifted; per-reason breakdown in <see cref="CapturesByReason"/>.</summary>
    public int CapturesLifted;
    public Dictionary<TriggerReason, int> CapturesByReason = new();

    public int SitesRewritten;
    public int ForeachPerIterationGroups;

    /// <summary>CS2J3201-3206 rows keyed by code; residual gate rows counted separately.</summary>
    public Dictionary<string, int> SkippedByReason = new(StringComparer.Ordinal);

    /// <summary>Mutable captures still present in the re-parsed intermediate tree (must be 0 in M2 strict).</summary>
    public int ResidualMutableCaptures;

    public void AddSkipped(string code)
        => SkippedByReason[code] = SkippedByReason.TryGetValue(code, out var count) ? count + 1 : 1;
}
