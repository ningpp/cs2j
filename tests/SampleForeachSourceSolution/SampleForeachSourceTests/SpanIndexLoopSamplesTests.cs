using System;
using SampleForeachSourceLib;
using Xunit;

namespace SampleForeachSourceTests;

/// <summary>
/// Runtime-equivalence assertions for the C40 foreach-source M2 FA-5
/// intermediate tree: plain foreach over Span&lt;T&gt;/ReadOnlySpan&lt;T&gt;
/// runs on the lowered cached-source + index + Length + indexer loop.
/// </summary>
public class SpanIndexLoopSamplesTests
{
    [Fact]
    public void SumSpan_EnumeratesAllValues()
    {
        var values = new[] { 1, 2, 3, 4 };

        Assert.Equal(10, SpanIndexLoopSamples.SumSpan(values));
    }

    [Fact]
    public void SumReadOnlySpan_EnumeratesAllValues()
    {
        ReadOnlySpan<int> values = new[] { 5, 6, 7 };

        Assert.Equal(18, SpanIndexLoopSamples.SumReadOnlySpan(values));
    }

    [Fact]
    public void Accumulate_ContinuesPastNegatives_AndBreaksAtSentinel()
    {
        var values = new[] { 1, -2, 3, 0, 100 };

        Assert.Equal(4, SpanIndexLoopSamples.AccumulatePositiveUntilSentinel(values));
    }

    [Fact]
    public void Nested_SpanOuter_DuckInner_LowersBothAndEnumerates()
    {
        var values = new[] { 1, 2 };

        var result = SpanIndexLoopSamples.NestedSpanOuterDuckInner(
            values, new StatefulStructDuck(2));

        // 1*10+1, 1*10+2, 2*10+1, 2*10+2
        Assert.Equal(new[] { 11, 12, 21, 22 }, result);
    }
}
