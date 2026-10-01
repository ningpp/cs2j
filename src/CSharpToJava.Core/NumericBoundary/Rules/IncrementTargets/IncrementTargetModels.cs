namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// C17 per-file statistics buckets (design §4.6). M0 ships the classifier only:
/// every bucket stays zero — no rewrite, no diagnostic, no counter moves.
/// M1 starts filling the site/temp/deferred buckets from Pass1/Pass2.
/// </summary>
public sealed class IncrementTargetStatistics
{
    // ── target-shape site buckets ──

    public int PropertySites;
    public int IndexerSites;

    /// <summary>Array-element sites whose element type is enum/decimal/custom-inc (decision 15).</summary>
    public int SpecialArrayElementSites;

    // ── arithmetic-core site buckets (enum split: real-enum cast vs C05 FromValue) ──

    public int EnumCastSites;
    public int EnumFromValueSites;
    public int DecimalSites;

    /// <summary>E09 residuals: unsigned/narrow sites E09 could not own (decision 9).</summary>
    public int UnsignedResidualSites;

    public int CustomOperatorSites;

    /// <summary>ref-returning targets expanded via ref alias (decision 16).</summary>
    public int RefAliasSites;

    // ── shape/lift buckets ──

    /// <summary>Prefix sites whose value is consumed inline (IT-10).</summary>
    public int PrefixInlineSites;

    public int PostfixStandaloneSites;
    public int PostfixLiftedSites;

    /// <summary>
    /// Event <c>++/--</c> sites — unreachable in compilable input (CS0079);
    /// steady state must be 0 (defensive bucket, design decision 5).
    /// </summary>
    public int EventSites;

    /// <summary>Hoisted temporaries grouped by role (receiver/indexArg/old/new/refAlias).</summary>
    public Dictionary<string, int> TempsByRole { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Sites deliberately failed/deferred, grouped by diagnostic code/reason.</summary>
    public Dictionary<string, int> DeferredByReason { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Aggregate of the owned site buckets. <see cref="EventSites"/> is excluded:
    /// an event site is never an owned increment site (events only accept +=/-=),
    /// the bucket is a defensive error counter rather than workload. The three
    /// postfix/prefix shape buckets are also excluded because they are an
    /// orthogonal re-cut of the target/core site buckets.
    /// </summary>
    public int TotalSites =>
        PropertySites
        + IndexerSites
        + SpecialArrayElementSites
        + EnumCastSites
        + EnumFromValueSites
        + DecimalSites
        + UnsignedResidualSites
        + CustomOperatorSites
        + RefAliasSites;
}
