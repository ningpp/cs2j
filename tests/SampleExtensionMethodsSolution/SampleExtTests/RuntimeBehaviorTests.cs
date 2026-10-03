using SampleExtLib.Collections;
using SampleExtLib.Delegates;
using SampleExtLib.NullGuards;
using SampleExtLib.Shapes;
using SampleExtLib.Spans;
using SampleExtLib.Text;
using Xunit;

namespace SampleExtTests;

/// <summary>
/// Runtime semantics proof: after the extension-methods pass rewrites every
/// reduced call into a global-qualified static call, behavior must be
/// identical (the CLI runner executes these on the intermediate tree).
/// </summary>
public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void PlainAndRepeatedExtensions_WorkAsSugar()
    {
        Assert.Equal("A!", "a".Shout());
        Assert.Equal("abab", "ab".Repeat(2));
    }

    [Fact]
    public void GenericExtension_InfersTypeArguments()
    {
        Assert.Equal("s", "s".Echo());
        Assert.Equal(42, 42.Echo());
    }

    [Fact]
    public void ExtensionChain_Composes()
    {
        Assert.Equal("GO!", "go".Echo().Shout());
    }

    [Fact]
    public void InstanceMethodSameName_Wins()
    {
        Assert.Equal(1, new Counter().Report());
    }

    [Fact]
    public void InstanceNotApplicable_FallsBackToExtension()
    {
        Assert.Equal(8, new Counter().Report(6));
    }

    [Fact]
    public void NullReceiver_ThrowsArgumentNullException()
    {
        string[]? missing = null;
        Assert.Throws<ArgumentNullException>(() => missing.RequireNonEmpty().ToList());
    }

    [Fact]
    public void EnumerableExtensions_ProcessReceiver()
    {
        var items = new[] { "a", "b", "c" }.RequireNonEmpty();
        Assert.Equal("a-b-c", items.JoinWith("-"));
        Assert.Throws<ArgumentException>(() => Array.Empty<string>().RequireNonEmpty().ToList());
    }

    [Fact]
    public void NullReceiver_ExtensionWithoutDereference_DoesNotThrow()
    {
        string? missing = null;
        Assert.True(missing.IsNull());
        Assert.False("value".IsNull());
    }

    [Fact]
    public void RefSpanExtension_MutatesCallerSpan()
    {
        Span<int> values = stackalloc int[2];
        values[0] = 1;
        values.IncrementFirst();
        Assert.Equal(2, values[0]);
        Assert.Equal(2, values.Sum());
    }

    [Fact]
    public void NullConditionalExtension_LiftsAndEvaluatesOnce()
    {
        string? missing = null;
        Assert.Null(missing?.LengthOrNull());
        Assert.Equal(3, "abc"?.LengthOrNull());
    }

    [Fact]
    public void MethodGroupExtension_CapturesReceiverInLambda()
    {
        var name = "x";
        Action greet = name.Greet;
        var before = GreetingExtensions.GreetingCount;
        greet();
        Assert.Equal(before + 1, GreetingExtensions.GreetingCount);
    }
}
