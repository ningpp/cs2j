namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// C51 assignment terminal statistics (design §5): site buckets by target
/// shape, per-operator compound buckets, coalesce/value/chain counters and
/// assignment-specific skip reasons. Merged into the same
/// <see cref="NullConditionalStatistics"/> object so the section keeps one
/// identity and stays observable only while the rule subsection is present.
/// </summary>
public sealed partial class NullConditionalStatistics
{
    /// <summary>Member (incl. property/ref-returning method) assignment sites (NA-1).</summary>
    public int AssignmentMemberSites;

    /// <summary>Index assignment sites (NA-2).</summary>
    public int AssignmentIndexSites;

    /// <summary>Compound (non-event) assignment sites (NA-3).</summary>
    public int CompoundAssignmentSites;

    /// <summary>Compound assignment sites per operator label (<c>+</c>, <c>&lt;&lt;</c> ...).</summary>
    public readonly Dictionary<string, int> AssignmentByOperator = new();

    /// <summary>Event compound assignment sites (M5/NA-3 event sub-shape).</summary>
    public int EventCompoundSites;

    /// <summary>Null-coalescing assignment combination sites (NA-6 / CS2J0568).</summary>
    public int CoalesceAssignmentSites;

    /// <summary>Value-context assignment sites (NA-5).</summary>
    public int ValueFormAssignmentSites;

    /// <summary>Chain assignment sites (M7 ordinary tail / M8 multi-segment).</summary>
    public int AssignmentChainSites;

    /// <summary>Assignment sites whose RHS/index-argument tree contains a conditional chain (M11/M19).</summary>
    public int AssignmentRhsConditionalSites;

    /// <summary>Segment temps emitted for assignment receivers.</summary>
    public int AssignmentReceiverTempsLifted;

    /// <summary>Assignment sites pointing at static members (M17; baseline rejects).</summary>
    public int StaticTargetSites;

    /// <summary>Assignment sites preserved/blocked by diagnostic code.</summary>
    public readonly Dictionary<string, int> SkippedAssignmentByReason = new();

    /// <summary>Adds one assignment-specific skip count.</summary>
    public void AddAssignmentSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;

        SkippedAssignmentByReason.TryGetValue(code, out var assignmentCount);
        SkippedAssignmentByReason[code] = assignmentCount + 1;
    }

    /// <summary>Merges the C51 assignment buckets (hook invoked by MergeFrom).</summary>
    partial void MergeAssignmentFrom(NullConditionalStatistics other)
    {
        AssignmentMemberSites += other.AssignmentMemberSites;
        AssignmentIndexSites += other.AssignmentIndexSites;
        CompoundAssignmentSites += other.CompoundAssignmentSites;
        EventCompoundSites += other.EventCompoundSites;
        CoalesceAssignmentSites += other.CoalesceAssignmentSites;
        ValueFormAssignmentSites += other.ValueFormAssignmentSites;
        AssignmentChainSites += other.AssignmentChainSites;
        AssignmentRhsConditionalSites += other.AssignmentRhsConditionalSites;
        AssignmentReceiverTempsLifted += other.AssignmentReceiverTempsLifted;
        StaticTargetSites += other.StaticTargetSites;
        MergeStringBuckets(AssignmentByOperator, other.AssignmentByOperator);
        MergeStringBuckets(SkippedAssignmentByReason, other.SkippedAssignmentByReason);
    }

    private static void MergeStringBuckets(
        Dictionary<string, int> target,
        Dictionary<string, int> source)
    {
        foreach (var pair in source)
        {
            target.TryGetValue(pair.Key, out var count);
            target[pair.Key] = count + pair.Value;
        }
    }
}
