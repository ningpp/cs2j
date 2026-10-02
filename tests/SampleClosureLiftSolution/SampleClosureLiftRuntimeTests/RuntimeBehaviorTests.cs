using SampleClosureLiftLib;
using Xunit;

namespace SampleClosureLiftRuntimeTests;

/// <summary>
/// Observable behavior assertions (design §7.2): counts, list contents and
/// call timing — never generated text. These run on the original fixture AND
/// the closure-lift intermediate tree (verifier), so they are the half-side
/// proof that holder lifting preserves C# closure semantics.
/// </summary>
public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void ExternalWriteCounter_StaysOneSharedCell()
        => Assert.Equal(12, CaptureScenarios.ExternalWriteCounter());

    [Fact]
    public void SharedDelegates_ObserveMixedWritesInOrder()
        => Assert.Equal((12, 22), CaptureScenarios.SharedDelegates());

    [Fact]
    public void ParameterCapture_FollowsReassignment()
        => Assert.Equal(102, CaptureScenarios.ParameterCapture(1));

    [Fact]
    public void NestedDelegates_ShareOuterCell()
        => Assert.Equal(41, CaptureScenarios.NestedDelegates());

    [Fact]
    public void ForInduction_IsOneSharedCell()
        => Assert.Equal(new[] { 3, 3, 3 }, CaptureScenarios.ForRoundValues(3));

    [Fact]
    public void ForeachIteration_IsFreshPerRound()
        => Assert.Equal(new[] { 1, 2, 3 },
            CaptureScenarios.ForeachRoundValues(new[] { 1, 2, 3 }));

    [Fact]
    public void LockedCounter_SurvivesLockSitesAndReassignment()
        => Assert.Equal(7, CaptureScenarios.LockedCounter());

    [Fact]
    public void GenericCapture_PreservesIntValues()
        => Assert.Equal(new[] { 1, 2 }, CaptureScenarios.GenericCapture(1, 2));

    [Fact]
    public void GenericCapture_PreservesReferenceValues()
        => Assert.Equal(new[] { "a", "b" }, CaptureScenarios.GenericCapture("a", "b"));

    [Fact]
    public void E02ShapedLiftedMethod_LiftsItsCaptureParameter()
        => Assert.Equal(2, CaptureScenarios.RunCs2jLiftedShape(0));

    [Fact]
    public void CapturelessControl_RunsUnhanged()
        => Assert.Equal(13, CapturelessControls.Run(3));

    [Fact]
    public void TwoThreads_AlternatingThroughOneDelegate_CountExactly()
        => Assert.Equal(200, ConcurrencyScenarios.PingPongCounter(100));
}
