namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// C35 null-conditional section statistics (design §6): site counters by
/// terminal shape, lifting counters, chain-length distribution and skip
/// reasons. The section is only observable when the rule is enabled (the
/// CLI summary segment is gated on the subsection presence).
/// </summary>
public sealed partial class NullConditionalStatistics
{
    /// <summary>Files analyzed with the rule active.</summary>
    public int FilesScanned;

    /// <summary>Member/value sites explicitized (NC-1/2/3, non-invoke value sites).</summary>
    public int ConditionalAccessSites;

    /// <summary>Conditional index <c>?[]</c> sites (NC-4).</summary>
    public int ConditionalIndexSites;

    /// <summary>Delegate/event <c>?.Invoke</c> sites (NC-5).</summary>
    public int InvokeSites;

    /// <summary>Sub-count of invoke sites whose receiver is an event symbol.</summary>
    public int EventSites;

    /// <summary>Sub-count of invoke sites whose receiver is a plain delegate.</summary>
    public int DelegateSites;

    /// <summary>void/statement sites rewritten to if form (NC-3 void/NC-8).</summary>
    public int VoidStatementSites;

    /// <summary>Sites with a lifted value-type terminal <c>T?</c> (NC-9).</summary>
    public int LiftedValueTypeSites;

    /// <summary>Sites composed with E03 <c>??</c> products (NC-6).</summary>
    public int CoalesceCompositionSites;

    /// <summary>Zero-declaration inlined sites (NC-1 whitelist).</summary>
    public int InlinedSites;

    /// <summary>Total segment temps emitted across chains.</summary>
    public int SegmentTempsLifted;

    /// <summary>Anchor statements replaced (void / guarded block sites).</summary>
    public int AnchorReplacements;

    /// <summary>Sites preserved for the later C51 assignment rule (NC-7 deferred).</summary>
    public int DeferredForC51Sites;

    /// <summary>Chain count by segment length.</summary>
    public readonly Dictionary<int, int> ChainsByLength = new();

    /// <summary>Sites skipped/kept by diagnostic code (code -&gt; count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    /// <summary>Residual trigger nodes counted by the post-lift gate.</summary>
    public int ResidualNodeCount;

    public void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }

    public void AddChainLength(int length)
    {
        ChainsByLength.TryGetValue(length, out var count);
        ChainsByLength[length] = count + 1;
    }

    public void MergeFrom(NullConditionalStatistics other)
    {
        FilesScanned += other.FilesScanned;
        ConditionalAccessSites += other.ConditionalAccessSites;
        ConditionalIndexSites += other.ConditionalIndexSites;
        InvokeSites += other.InvokeSites;
        EventSites += other.EventSites;
        DelegateSites += other.DelegateSites;
        VoidStatementSites += other.VoidStatementSites;
        LiftedValueTypeSites += other.LiftedValueTypeSites;
        CoalesceCompositionSites += other.CoalesceCompositionSites;
        InlinedSites += other.InlinedSites;
        SegmentTempsLifted += other.SegmentTempsLifted;
        AnchorReplacements += other.AnchorReplacements;
        DeferredForC51Sites += other.DeferredForC51Sites;
        ResidualNodeCount += other.ResidualNodeCount;
        MergeAssignmentFrom(other);
        MergeBuckets(ChainsByLength, other.ChainsByLength);
        MergeBuckets(SkippedByReason, other.SkippedByReason);
    }

    /// <summary>C51 assignment bucket merge hook (implementation in the Assignment part).</summary>
    partial void MergeAssignmentFrom(NullConditionalStatistics other);

    private static void MergeBuckets(
        Dictionary<int, int> target, Dictionary<int, int> source)
    {
        foreach (var pair in source)
        {
            target.TryGetValue(pair.Key, out var count);
            target[pair.Key] = count + pair.Value;
        }
    }

    private static void MergeBuckets(
        Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var pair in source)
        {
            target.TryGetValue(pair.Key, out var count);
            target[pair.Key] = count + pair.Value;
        }
    }
}
