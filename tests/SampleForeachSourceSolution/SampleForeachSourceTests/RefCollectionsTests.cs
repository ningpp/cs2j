using System;
using SampleForeachSourceLib;
using Xunit;

namespace SampleForeachSourceTests;

/// <summary>
/// Runtime-equivalence assertions for the C40 foreach-source M3
/// intermediate tree: FR-2 Span/ReadOnlySpan/custom Count+ref-indexer,
/// FR-3 ref-Current enumerator ref foreach sites lower and behave
/// exactly like the original ref iteration. (FR-1 array ref foreach is
/// rejected by csc with CS1510 and is covered by Core unit tests only.)
/// </summary>
public class RefCollectionsTests
{
    [Fact]
    public void DoubleSpan_WritableRefLoop_DoublesEveryElement()
    {
        var values = new[] { 1, 2, 3, 4 };

        RefCollections.DoubleSpan(values);

        Assert.Equal(new[] { 2, 4, 6, 8 }, values);
    }

    [Fact]
    public void SumReadOnlySpanRef_RefReadonlyLoop_SumsAllValues()
    {
        ReadOnlySpan<int> values = new[] { 5, 6, 7 };

        Assert.Equal(18, RefCollections.SumReadOnlySpanRef(values));
    }

    [Fact]
    public void DoubleCountIndexBag_CustomCountRefIndexer_DoublesBackingStore()
    {
        var backing = new[] { 1, 2, 3 };

        RefCollections.DoubleCountIndexBag(new CountIndexBag(backing));

        Assert.Equal(new[] { 2, 4, 6 }, backing);
    }

    [Fact]
    public void DoubleRefReadCollection_RefCurrentEnumerator_DoublesBackingStore()
    {
        var backing = new[] { 4, 5, 6 };

        RefCollections.DoubleRefReadCollection(new RefReadCollection(backing));

        Assert.Equal(new[] { 8, 10, 12 }, backing);
    }
}
