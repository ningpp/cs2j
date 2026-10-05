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

    /// <summary>TS-5 struct statement-level temp lifts actually emitted.</summary>
    public int StructTempLifts;

    public int InitializersNormalized;
    public int InfeasibleSites;

    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);
    public int ResidualThreadStaticAttributes;
    public int OwnedResidualAttributes;
    public int UnownedResidualAttributes;

    /// <summary>Field plans excluded by the rollback loop (re-attributed new compile errors).</summary>
    public int PlansRolledBack;

    /// <summary>Validation rounds executed by the rollback loop (1 when no rollback happened).</summary>
    public int RollbackRounds;

    /// <summary>1 when the run ended in a CS2J1200 whole-run rollback, otherwise 0.</summary>
    public int WholeRunRolledBack;

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
        OwnedResidualAttributes += source.OwnedResidualAttributes;
        UnownedResidualAttributes += source.UnownedResidualAttributes;
        PlansRolledBack += source.PlansRolledBack;
        RollbackRounds += source.RollbackRounds;
        WholeRunRolledBack += source.WholeRunRolledBack;
        foreach (var (key, value) in source.SkippedByReason)
        {
            SkippedByReason.TryGetValue(key, out var existing);
            SkippedByReason[key] = existing + value;
        }
    }

    public string SummaryLine()
    {
        var line =
            $"thread-static: {FilesTransformed} transformed, {FilesSkippedClean} clean " +
            $"({FieldsRewritten} fields rewritten, {FieldsSkipped} skipped, " +
            $"{ReadsRewritten} reads, {WritesRewritten} writes, " +
            $"{CompoundSitesRewritten} compound, {IncrementSitesRewritten} increments, " +
            $"{StructTempLifts} struct lifts, {InitializersNormalized} initializers, " +
            $"{PlansRolledBack} plan rollback(s), {ResidualThreadStaticAttributes} residual)";
        return WholeRunRolledBack > 0 ? line + " [WHOLE RUN ROLLED BACK: CS2J1200]" : line;
    }
}
