namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// C34 per-file statistics buckets (design §4.5). Every bucket stays zero while
/// <see cref="NumericBoundaryOptions.EnableDecimalOps"/> is false (dormant
/// landing): the decimal pipeline does not run and leaves no trace.
/// </summary>
public sealed class DecimalOpStatistics
{
    // ── binary arithmetic DC-1~DC-5, keys: add/sub/mul/div/rem ──
    public Dictionary<string, int> BinarySitesByOp { get; } =
        new(StringComparer.Ordinal);

    // ── comparisons DC-6/DC-7 ──
    public int EqualityComparisonSites;
    public int RelationalComparisonSites;

    // ── unary DC-8 ──
    public int UnaryNegateSites;
    public int UnaryPlusStrippedSites;

    // ── compound assignments DC-10/DC-11 ──
    public int SimpleCompoundSites;
    public int ComplexCompoundSites;

    // ── operand promotions DC-9 ──
    public int CastPromotionSites;
    public int ToDecimalUInt32PromotionSites;
    public int ToDecimalUInt64PromotionSites;

    // ── const downgrade DC-12 ──
    public int ConstantsDowngraded;

    // ── hoisted temporaries DC-11, keys: receiver/indexArg/value ──
    public Dictionary<string, int> TempsByRole { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Sites deliberately left untouched, grouped by reason (lifted/user-operator).</summary>
    public Dictionary<string, int> DeferredByReason { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Built-in decimal operator nodes still present in the output tree (DC-13; must be 0).</summary>
    public int ResidualDecimalOperators;

    public int TotalBinarySites =>
        BinarySitesByOp.Values.Sum();

    public int TotalComparisonSites =>
        EqualityComparisonSites + RelationalComparisonSites;

    public int TotalUnarySites =>
        UnaryNegateSites + UnaryPlusStrippedSites;

    public int TotalCompoundSites =>
        SimpleCompoundSites + ComplexCompoundSites;

    public int TotalPromotionSites =>
        CastPromotionSites + ToDecimalUInt32PromotionSites + ToDecimalUInt64PromotionSites;

    public int TotalTemps =>
        TempsByRole.Values.Sum();

    public int TotalDeferred =>
        DeferredByReason.Values.Sum();

    public void AddBinary(string opKey) =>
        BinarySitesByOp[opKey] = BinarySitesByOp.GetValueOrDefault(opKey) + 1;

    public void AddTemp(string role) =>
        TempsByRole[role] = TempsByRole.GetValueOrDefault(role) + 1;

    public void AddDeferred(string reason) =>
        DeferredByReason[reason] = DeferredByReason.GetValueOrDefault(reason) + 1;
}
