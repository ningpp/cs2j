namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>Counters for the thread-static pass (the adapter merges per-file results).</summary>
public sealed class ThreadStaticStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    public int FieldsDetected;
    public int FieldsRewritten;
    public int FieldsSkipped;

    public int ReadsRewritten;
    public int WritesRewritten;
    public int CompoundSitesRewritten;
    public int IncrementSitesRewritten;

    /// <summary>TS-5 struct temp lifts (M2; always 0 in M1).</summary>
    public int StructTempLifts;

    public int InitializersNormalized;
    public int InfeasibleSites;

    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);
    public int ResidualThreadStaticAttributes;

    public void Merge(ThreadStaticStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesSkippedClean += source.FilesSkippedClean;
        FieldsDetected += source.FieldsDetected;
        FieldsRewritten += source.FieldsRewritten;
        FieldsSkipped += source.FieldsSkipped;
        ReadsRewritten += source.ReadsRewritten;
        WritesRewritten += source.WritesRewritten;
        CompoundSitesRewritten += source.CompoundSitesRewritten;
        IncrementSitesRewritten += source.IncrementSitesRewritten;
        StructTempLifts += source.StructTempLifts;
        InitializersNormalized += source.InitializersNormalized;
        InfeasibleSites += source.InfeasibleSites;
        ResidualThreadStaticAttributes += source.ResidualThreadStaticAttributes;
        foreach (var (key, value) in source.SkippedByReason)
        {
            SkippedByReason.TryGetValue(key, out var existing);
            SkippedByReason[key] = existing + value;
        }
    }

    public string SummaryLine()
        => $"thread-static: {FilesTransformed} transformed, {FilesSkippedClean} clean " +
           $"({FieldsRewritten} fields rewritten, {FieldsSkipped} skipped, " +
           $"{ReadsRewritten} reads, {WritesRewritten} writes, " +
           $"{CompoundSitesRewritten} compound, {IncrementSitesRewritten} increments, " +
           $"{InitializersNormalized} initializers, {ResidualThreadStaticAttributes} residual)";
}
