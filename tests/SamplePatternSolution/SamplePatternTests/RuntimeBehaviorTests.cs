using SamplePatternLib;
using Xunit;

namespace SamplePatternTests;

/// <summary>
/// Runtime behavior of the desugared constructs. These assertions run against the
/// intermediate tree produced by the pattern-desugar pass, so they prove the
/// if-chain / guarded-catch lowering preserved C# semantics (not just shape).
/// </summary>
public class RuntimeBehaviorTests
{
    [Theory]
    [InlineData("abcd", "long-string")]
    [InlineData("ab", "string")]
    [InlineData(5, "positive")]
    [InlineData(-5, "non-positive-int")]
    [InlineData(null, "null")]
    [InlineData(1, "boxed-one-or-two")]
    [InlineData(2, "boxed-one-or-two")]
    [InlineData(true, "true")]
    [InlineData(3.5, "other")]
    public void ClassifyObject_MatchesEachBranch(object? o, string expected)
        => Assert.Equal(expected, PatternSwitchSamples.ClassifyObject(o));

    [Fact]
    public void BoolAndLongGoverningSwitches_Match()
    {
        Assert.Equal("yes", PatternSwitchSamples.ClassifyBool(true));
        Assert.Equal("no", PatternSwitchSamples.ClassifyBool(false));
        Assert.Equal("zero", PatternSwitchSamples.ClassifyLong(0L));
        Assert.Equal("answer", PatternSwitchSamples.ClassifyLong(42L));
        Assert.Equal("other", PatternSwitchSamples.ClassifyLong(7L));
    }

    [Fact]
    public void NestedLoopBreak_KeepsInnerBreakSemantics()
        => Assert.Equal(6, PatternSwitchSamples.NestedLoopBreakInsidePatternCase(5));

    [Theory]
    [InlineData(5, true, 101)]
    [InlineData(5, false, 6)]
    [InlineData("x", false, 0)]
    public void MidSectionBreak_JumpsPastSwitch(object? o, bool stopEarly, int expected)
        => Assert.Equal(expected, PatternSwitchSamples.MidSectionBreak(o, stopEarly));

    [Theory]
    [InlineData(0, 0, "origin")]
    [InlineData(0, 5, "y-axis")]
    [InlineData(1, 9, "x-is-one")]
    [InlineData(2, 9, "other")]
    [InlineData(-1, 0, "null")] // -1 selects the null-point case below
    public void PropertyPattern_BranchesMatch(int x, int y, string expected)
        => Assert.Equal(expected, PropertyPatternSamples.Describe(
            x == -1 ? null : new Point { X = x, Y = y }));

    [Fact]
    public void SameTypeTwoCatches_FilterFalseFallsToSecondClause()
    {
        Assert.Equal("filtered", CatchSamples.FilterThenSameTypeFallback(true));
        Assert.Equal("fallback", CatchSamples.FilterThenSameTypeFallback(false));
    }

    [Fact]
    public void FilterFalse_PropagatesToOuterHandler()
        => Assert.Equal("propagated:no-match", CatchSamples.FilterFalsePropagates());

    [Theory]
    [InlineData(0, "format-first")]
    [InlineData(1, "argument")]
    [InlineData(2, "other:operation")]
    public void MultipleFilters_ResolveInSourceOrder(int kind, string expected)
        => Assert.Equal(expected, CatchSamples.MultipleFiltersInOrder(kind));

    [Fact]
    public void FinallyRuns_BeforePropagationDecision()
    {
        Assert.Equal("caught;finally;", CatchSamples.FilterWithFinally(true));
        Assert.Equal("finally;propagated;", CatchSamples.FilterWithFinally(false));
    }

    [Fact]
    public void CatchTypeParameter_WithFilter_CatchesDerivedException()
    {
        Assert.Equal("typed-catch",
            CatchSamples.CatchTypeParameter(new InvalidOperationException("typed")));
    }

    [Fact]
    public void CrossFileConsumer_SeesLoweredBehavior()
        => Assert.Equal("consumed:positive", CrossFileConsumer.Consume(7));
}
