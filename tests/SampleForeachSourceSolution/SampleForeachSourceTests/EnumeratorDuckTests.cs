using System;
using System.Collections.Generic;
using SampleForeachSourceLib;
using Xunit;

namespace SampleForeachSourceTests;

/// <summary>
/// Runtime-equivalence assertions for the C40 foreach-source M1 intermediate
/// tree (FA-1/FA-2/FA-4). The chain runs dotnet test on the rewritten tree,
/// so plain-duck enumeration, Dispose timing (normal/break/exception/
/// continue), single source evaluation and per-iteration lambda captures are
/// all verified against the lowered GetEnumerator/while skeleton.
/// </summary>
public class EnumeratorDuckTests
{
    [Fact]
    public void PlainDuck_EnumeratesAllValues_WithoutDispose()
    {
        Assert.Equal(new[] { 1, 2, 3 },
            EnumeratorDuckSamples.CollectPlain(new PlainDuck()));
    }

    [Fact]
    public void Source_IsEvaluatedExactlyOnce()
    {
        var factory = new PlainDuckFactory();

        var values = EnumeratorDuckSamples.CollectFromFactory(factory);

        Assert.Equal(new[] { 1, 2, 3 }, values);
        Assert.Equal(1, factory.SourceCallCount);
    }

    [Fact]
    public void PatternDispose_WithoutIDisposable_IsNeverCalled()
    {
        var duck = new PatternDisposeDuck();

        var values = new List<int>();
        foreach (var item in duck)
        {
            values.Add(item);
        }

        Assert.Equal(new[] { 1, 2 }, values);
        Assert.Equal(0, duck.Enumerator.DisposeCount);
    }

    [Fact]
    public void IDisposable_NormalCompletion_DisposesExactlyOnce()
    {
        var duck = new FlaggedDuck();

        Assert.Equal(1, EnumeratorDuckSamples.NormalCompletionDisposeCount(duck));
        Assert.Equal(1, duck.Enumerator.DisposeCount);
    }

    [Fact]
    public void IDisposable_Break_DisposesExactlyOnce_AfterOnlyFirstItem()
    {
        var duck = new FlaggedDuck();
        var seen = new List<int>();

        var disposeCount = EnumeratorDuckSamples.BreakAfterFirst(duck, seen);

        Assert.Equal(new[] { 1 }, seen);
        Assert.Equal(1, disposeCount);
        Assert.Equal(1, duck.Enumerator.DisposeCount);
    }

    [Fact]
    public void IDisposable_BodyException_DisposesExactlyOnce_AndBubbles()
    {
        var duck = new FlaggedDuck();

        var ex = Assert.Throws<InvalidOperationException>(
            () => EnumeratorDuckSamples.ThrowAtSecond(duck));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(1, duck.Enumerator.DisposeCount);
    }

    [Fact]
    public void Continue_DoesNotDisposeMidLoop_DisposesOnceAtEnd()
    {
        var duck = new FlaggedDuck();
        var midCounts = new List<int>();

        var finalCount = EnumeratorDuckSamples.ContinueObservations(duck, midCounts);

        Assert.Equal(new[] { 0, 0, 0 }, midCounts);
        Assert.Equal(1, finalCount);
    }

    [Fact]
    public void ExtensionGetEnumerator_EnumeratesAllValues()
    {
        var values = EnumeratorDuckSamples.CollectExtension(
            new ExtensionEnumerable(4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, values);
    }

    [Fact]
    public void InterfaceTypedEnumerator_GenericAndNonGeneric_EnumerateValues()
    {
        Assert.Equal(new[] { 1, 2, 3 },
            EnumeratorDuckSamples.CollectGenericBoxed(
                new GenericBoxedEnumerable(1, 2, 3)));

        Assert.Equal(new[] { 4, 5 },
            EnumeratorDuckSamples.CollectNonGenericBoxed(
                new NonGenericBoxedEnumerable(4, 5)));
    }

    [Fact]
    public void LambdaCapturedInBody_SeesItsOwnIterationValue()
    {
        var values = EnumeratorDuckSamples.CaptureActions(new PlainDuck());

        Assert.Equal(new[] { 1, 2, 3 }, values);
    }
}
