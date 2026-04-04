using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: LINQ procedural rewrite generates _linqitems.Count on a
/// helper-method parameter typed as IEnumerable&lt;T&gt; (→ Iterable in Java).
///
/// Root cause: GetCollectionCount checks the ORIGINAL collection's type
/// (e.g. IList&lt;T&gt; which implements ICollection&lt;T&gt;) and emits
/// _linqitems.Count. But the helper-method parameter is typed as
/// IEnumerable&lt;T&gt; (non-indexed foreach loop), so the rebuilt semantic
/// model cannot resolve .Count on IEnumerable. The converter then falls
/// through to the method-group handler producing _linqitems::count, which
/// is invalid Java on Iterable.
/// </summary>
public class LinqCountOnIterableTests
{
    /// <summary>
    /// Reproduces the AGL bug: IList&lt;IEdge&gt; implements ICollection
    /// so GetCollectionCount emits _linqitems.Count, but the parameter
    /// type is IEnumerable&lt;T&gt; → Iterable, which has no count().
    /// </summary>
    [Fact]
    public void SelectToList_OnIList_ShouldNotGenerateCountOnIterable()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

interface IEdge { int Source { get; } int Target { get; } }

class OverlappedEdge
{
    public int Weight { get; set; }
}

class TreeBuilder
{
    IList<IEdge> GetTreeEdges() { return null; }
    Dictionary<int, OverlappedEdge> weighting = new Dictionary<int, OverlappedEdge>();

    List<OverlappedEdge> BuildTree()
    {
        return GetTreeEdges().Select(e => weighting[e.Source]).ToList();
    }
}");

        Assert.True(result.Success);
        // Must NOT contain ::count (invalid method reference on Iterable)
        Assert.DoesNotContain("::count", result.GeneratedCode, StringComparison.Ordinal);
        // Must NOT contain .count() call on Iterable parameter
        Assert.DoesNotContain("_linqitems.count()", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// List&lt;T&gt; uses indexed for-loop with concrete parameter type,
    /// so .size() (mapped from .Count) is valid and should still work.
    /// </summary>
    [Fact]
    public void SelectToList_OnList_ShouldCompileCorrectly()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Item { public int Value { get; set; } }

class Processor
{
    List<Item> items = new List<Item>();

    List<int> GetValues()
    {
        return items.Select(i => i.Value).ToList();
    }
}");

        Assert.True(result.Success);
        // Should not have invalid Iterable.count() references
        Assert.DoesNotContain("::count", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// ICollection&lt;T&gt; (not List) uses foreach with IEnumerable parameter.
    /// _linqitems.Count must not be emitted.
    /// </summary>
    [Fact]
    public void SelectToList_OnICollection_ShouldNotGenerateCountOnIterable()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Processor
{
    ICollection<int> GetNumbers() { return null; }

    List<string> Format()
    {
        return GetNumbers().Select(n => n.ToString()).ToList();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("::count", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_linqitems.count()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }
}
