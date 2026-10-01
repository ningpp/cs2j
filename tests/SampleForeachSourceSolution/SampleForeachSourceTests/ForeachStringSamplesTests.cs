using System;
using System.Collections.Generic;
using System.Linq;
using SampleForeachSourceLib;
using Xunit;

namespace SampleForeachSourceTests;

/// <summary>
/// Runtime-equivalence assertions for the C16 foreach-source intermediate
/// tree. The chain runs dotnet test on the rewritten tree, so FS-1 behavior
/// (single evaluation, UTF-16 code-unit semantics, nesting, NRE timing,
/// empty strings, explicit element conversions) is verified on the lowered
/// output itself.
/// </summary>
public class ForeachStringSamplesTests
{
    [Fact]
    public void Collect_ParameterSource_EnumeratesEveryChar()
    {
        Assert.Equal(new[] { 'a', 'b', 'c' }, ForeachStringSamples.Collect("abc"));
    }

    [Fact]
    public void ExplicitChar_BehavesLikeVar()
    {
        Assert.Equal(ForeachStringSamples.Collect("xyz"),
            ForeachStringSamples.ExplicitChar("xyz"));
    }

    [Fact]
    public void CodeUnits_SurrogatePair_CountsUtf16CodeUnits()
    {
        var units = ForeachStringSamples.CodeUnits("a😊b");
        // 'a' + high surrogate + low surrogate + 'b' => 4 UTF-16 code units.
        Assert.Equal(new[] { 97, 0xD83D, 0xDE0A, 98 }, units);
        Assert.Equal(4, units.Count);
    }

    [Fact]
    public void BoxedChars_AreCharBoxes()
    {
        var boxed = ForeachStringSamples.BoxedChars("ab");
        Assert.Equal(2, boxed.Count);
        Assert.Equal('a', Assert.IsType<char>(boxed[0]));
        Assert.Equal('b', Assert.IsType<char>(boxed[1]));
    }

    [Fact]
    public void Nested_InnerStringSites_WorkIdentically()
    {
        Assert.Equal(new[] { 'a', 'b', 'c', 'd' },
            ForeachStringSamples.Nested(new[] { "ab", "cd" }));
    }

    [Fact]
    public void NonStringSites_PassThroughUnchanged()
    {
        var list = new List<char> { '1', '2' };
        var array = new[] { '3', '4' };
        Assert.Equal(new[] { '1', '2', '3', '4' },
            ForeachStringSamples.NonStringSites(list, array));
    }

    [Fact]
    public void EmptyString_IteratesZeroTimes()
    {
        Assert.Equal(0, ForeachStringSamples.EmptyIterations(""));
    }

    [Fact]
    public void NullParameter_ThrowsAtLoopEntry()
    {
        Assert.Throws<NullReferenceException>(
            () => ForeachStringSamples.ThrowOnNullParameter(null));
    }

    [Fact]
    public void NullReturningMethod_ThrowsAtLoopEntry()
    {
        Assert.Throws<NullReferenceException>(
            ForeachStringSamples.ThrowOnNullReturn);
    }

    [Fact]
    public void SourceExpression_IsEvaluatedExactlyOnce()
    {
        var samples = new ForeachStringSamples();
        var chars = samples.SingleEvaluation();
        Assert.Equal(new[] { 'o', 'n', 'c', 'e' }, chars);
        Assert.Equal(1, samples.GetTextCallCount);
    }
}
