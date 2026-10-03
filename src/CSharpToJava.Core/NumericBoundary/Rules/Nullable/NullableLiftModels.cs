namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 per-file statistics buckets (design §4.7). M0 ships the rule seam and
/// the read-only classifier only: every bucket stays zero — no rewrite, no
/// diagnostic, no counter moves (potential sites are counted into method-local
/// variables only). M1 starts filling the site/temp buckets from Pass1/Pass2;
/// M2 adds conversions/GOV/increment/enum-backstop and the GuardedCheckedInner
/// co-run tally with C15.
/// </summary>
public sealed class NullableLiftStatistics
{
    // ── lifted operator site buckets (NL-1/NL-2) ──

    /// <summary>NL-1 lifted binary arithmetic/bit/shift sites (<c>+ - * / % &amp; | ^ &lt;&lt; &gt;&gt;</c>).</summary>
    public int LiftedBinarySites;

    /// <summary>NL-2 lifted unary sites (unary <c>- + ~</c>; lifted <c>!</c> lives in <see cref="LiftedBoolLogicNotSites"/>).</summary>
    public int LiftedUnarySites;

    /// <summary>NL-3 lifted relational sites (<c>&lt; &gt; &lt;= &gt;=</c>; result is non-nullable <c>bool</c>).</summary>
    public int LiftedRelationalSites;

    // ── NL-4 lifted equality, split by operand shape (§4.7) ──

    /// <summary>NL-4 equality sites compared against a <c>null</c> literal (<c>x == null</c>).</summary>
    public int LiftedEqualityNullLiteralSites;

    /// <summary>NL-4 equality sites with exactly one nullable operand and one non-nullable value.</summary>
    public int LiftedEqualityOneSidedSites;

    /// <summary>NL-4 equality sites with both operands <c>T?</c> (double-null special case).</summary>
    public int LiftedEqualityBothNullableSites;

    // ── NL-5 bool? three-valued logic (§4.7 And/Or/Xor/Not split) ──

    /// <summary>NL-5 lifted <c>&amp;</c> on <c>bool?</c> (Kleene absorption, decision 6).</summary>
    public int LiftedBoolLogicAndSites;

    /// <summary>NL-5 lifted <c>|</c> on <c>bool?</c> (Kleene absorption, decision 7).</summary>
    public int LiftedBoolLogicOrSites;

    /// <summary>NL-5 lifted <c>^</c> on <c>bool?</c> (plain null propagation, decision 8).</summary>
    public int LiftedBoolLogicXorSites;

    /// <summary>NL-5 lifted <c>!</c> on <c>bool?</c>.</summary>
    public int LiftedBoolLogicNotSites;

    // ── member / conversion sites (NL-6/NL-7/NL-8) ──

    /// <summary>NL-6 <c>GetValueOrDefault()</c>/<c>GetValueOrDefault(d)</c> call sites.</summary>
    public int GetValueOrDefaultSites;

    /// <summary>NL-7 <c>T? → T</c> unwrap conversion sites rewritten to <c>.Value</c>.</summary>
    public int UnwrapConversions;

    /// <summary>NL-8 lifted <c>S? → T?</c> conversion sites guarded with HasValue/Value.</summary>
    public int LiftedConversions;

    // ── NL-10/NL-11 lifted increment / compound assignment ──

    /// <summary>NL-10 lifted prefix <c>++x/--x</c> simple-target sites.</summary>
    public int LiftedIncrementPrefixSites;

    /// <summary>NL-10 lifted postfix <c>x++/x--</c> simple-target sites.</summary>
    public int LiftedIncrementPostfixSites;

    /// <summary>NL-11 lifted arithmetic/bit compound assignment sites (<c>x += y</c> …).</summary>
    public int LiftedIncrementCompoundSites;

    // ── NL-9 / co-run / lifting ──

    /// <summary>
    /// NL-9 backstop sites: lifted enum bit operations / <c>~</c> / bit compound
    /// assignments left behind by E04's unstable-reference skip, rewritten here.
    /// Stable E04 ternary products are never counted (they are not lifted nodes).
    /// </summary>
    public int NullableEnumBackstopSites;

    /// <summary>
    /// Guarded inner sites whose bare <c>T</c> arithmetic/conversion was taken
    /// over by the C15 post-sweep (checked helpers); C22/C15 co-run tally
    /// (decision 18; stays 0 until M2).
    /// </summary>
    public int GuardedCheckedInnerSites;

    /// <summary>
    /// Temporaries hoisted by Pass2 (unstable operands / postfix old values /
    /// GOV defaults), split by role (design §4.5): <c>a</c> left operand /
    /// receiver, <c>b</c> right operand, <c>o</c> postfix old value,
    /// <c>d</c> GOV default argument.
    /// </summary>
    public int TempsLeftRole;

    /// <summary>Hoisted right-operand temporaries (role <c>b</c>).</summary>
    public int TempsRightRole;

    /// <summary>Hoisted postfix old-value temporaries (role <c>o</c>).</summary>
    public int TempsOldRole;

    /// <summary>Hoisted GOV default-argument temporaries (role <c>d</c>).</summary>
    public int TempsDefaultRole;

    /// <summary>Temporaries hoisted by Pass2 (unstable operands / postfix old values / GOV defaults).</summary>
    public int TempsLifted;

    /// <summary>Sites deliberately failed/deferred, grouped by diagnostic code/reason (CS2J5352-5357).</summary>
    public Dictionary<string, int> DeferredByReason { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Aggregate equality bucket across the three operand-shape splits
    /// (NullLiteral/OneSided/BothNullable).
    /// </summary>
    public int LiftedEqualitySites =>
        LiftedEqualityNullLiteralSites
        + LiftedEqualityOneSidedSites
        + LiftedEqualityBothNullableSites;

    /// <summary>Aggregate <c>bool?</c> logic bucket across And/Or/Xor/Not.</summary>
    public int LiftedBoolLogicSites =>
        LiftedBoolLogicAndSites
        + LiftedBoolLogicOrSites
        + LiftedBoolLogicXorSites
        + LiftedBoolLogicNotSites;

    /// <summary>Aggregate lifted increment bucket across Prefix/Postfix/Compound.</summary>
    public int LiftedIncrementSites =>
        LiftedIncrementPrefixSites
        + LiftedIncrementPostfixSites
        + LiftedIncrementCompoundSites;
}
