namespace CSharpToJava.Core.GenericConstraint;

/// <summary>
/// Per-rule counters for the generic-constraint pass. The adapter merges the
/// per-file results; <see cref="SummaryLine"/> keeps the chain summary stable
/// and auditable with a <c>generic-constraint:</c> prefix plus per-rule numbers.
/// </summary>
public sealed class GenericConstraintStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    /// <summary>Every site of the rule family that the analyzer saw.</summary>
    public Dictionary<GenericConstraintRule, int> Encountered { get; } = NewCounters();

    /// <summary>Sites the pass actually rewrote.</summary>
    public Dictionary<GenericConstraintRule, int> Rewritten { get; } = NewCounters();

    /// <summary>Sites kept verbatim (reference/unconstrained or unprovable).</summary>
    public Dictionary<GenericConstraintRule, int> Preserved { get; } = NewCounters();

    /// <summary>Sites declared unsupported (CN-08 multi-dim array, CN-11 Enum constraint).</summary>
    public Dictionary<GenericConstraintRule, int> Unsupported { get; } = NewCounters();

    public void CountEncountered(GenericConstraintRule rule) => Increment(Encountered, rule);

    public void CountRewritten(GenericConstraintRule rule)
    {
        Increment(Encountered, rule);
        Increment(Rewritten, rule);
    }

    public void CountPreserved(GenericConstraintRule rule)
    {
        Increment(Encountered, rule);
        Increment(Preserved, rule);
    }

    public void CountUnsupported(GenericConstraintRule rule)
    {
        Increment(Encountered, rule);
        Increment(Unsupported, rule);
    }

    public void Merge(GenericConstraintStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesSkippedClean += source.FilesSkippedClean;
        MergeCounters(Encountered, source.Encountered);
        MergeCounters(Rewritten, source.Rewritten);
        MergeCounters(Preserved, source.Preserved);
        MergeCounters(Unsupported, source.Unsupported);
    }

    public string SummaryLine()
    {
        var rules = string.Join(", ", Enum.GetValues<GenericConstraintRule>().Select(rule =>
            $"{rule} {Encountered[rule]}/{Rewritten[rule]}/{Preserved[rule]}/{Unsupported[rule]}"));

        return $"generic-constraint: {FilesTransformed} transformed, {FilesSkippedClean} clean " +
               $"(encountered {Encountered.Values.Sum()}, rewritten {Rewritten.Values.Sum()}, " +
               $"preserved {Preserved.Values.Sum()}, unsupported {Unsupported.Values.Sum()}; " +
               $"{rules})";
    }

    private static Dictionary<GenericConstraintRule, int> NewCounters()
        => Enum.GetValues<GenericConstraintRule>()
            .ToDictionary(rule => rule, _ => 0);

    private static void Increment(Dictionary<GenericConstraintRule, int> counters, GenericConstraintRule rule)
        => counters[rule] = counters.TryGetValue(rule, out var existing) ? existing + 1 : 1;

    private static void MergeCounters(
        Dictionary<GenericConstraintRule, int> target,
        Dictionary<GenericConstraintRule, int> source)
    {
        foreach (var (rule, value) in source)
        {
            target[rule] = (target.TryGetValue(rule, out var existing) ? existing : 0) + value;
        }
    }
}
