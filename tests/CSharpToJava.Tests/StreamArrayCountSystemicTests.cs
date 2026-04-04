using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Systemic tests for three Java compilation errors:
/// 1. Stream&lt;T&gt; cannot be converted to Iterable&lt;T&gt; (expression-bodied returns)
/// 2. T[] cannot be converted to Iterable&lt;T&gt; (expression-bodied returns)
/// 3. Cannot find symbol: variable Count on Collection&lt;T&gt; (missing IReadOnlyCollection mappings)
/// </summary>
public class StreamArrayCountSystemicTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Error 1: Stream<T> → Iterable<T> — expression-bodied methods/properties
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExpressionBodiedMethod_ReturningLinqChain_CollectsStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Graph
{
    private List<string> edges = new List<string>();
    public IEnumerable<string> GetEdges() => edges.Where(e => e.Length > 0);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The LINQ .Where() becomes .stream().filter() — must be collected for Iterable return
        Assert.Contains(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedProperty_ReturningLinqChain_CollectsStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Graph
{
    private List<string> edges = new List<string>();
    public IEnumerable<string> Edges => edges.Where(e => e.Length > 0);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedMethod_ReturningSelect_CollectsStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node { public int Id; }
class Graph
{
    private List<Node> nodes = new List<Node>();
    public IEnumerable<int> GetNodeIds() => nodes.Select(n => n.Id);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedProperty_GetterWithArrow_ReturningLinq_CollectsStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Graph
{
    private List<string> items = new List<string>();
    public IEnumerable<string> Items { get => items.Where(x => x != null); }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Error 2: T[] → Iterable<T> — expression-bodied methods/properties
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExpressionBodiedMethod_ReturningArray_WrapsForIterable()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Graph
{
    private string[] nodes;
    public IEnumerable<string> GetNodes() => nodes;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // String[] (reference type) → Arrays.asList()
        Assert.Contains("Arrays.asList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedProperty_ReturningArray_WrapsForIterable()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Graph
{
    private string[] nodes;
    public IEnumerable<string> Nodes => nodes;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Arrays.asList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedMethod_ReturningPrimitiveArray_BoxesForIterable()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    private int[] values;
    public IEnumerable<int> GetValues() => values;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Primitive int[] needs boxing via Arrays.stream().boxed().collect()
        Assert.Contains("Arrays.stream(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Error 3: Count property on IReadOnlyCollection / IReadOnlyList / ISet
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void IReadOnlyCollectionCount_MapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(IReadOnlyCollection<string> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Count", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IReadOnlyListCount_MapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(IReadOnlyList<string> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ISetCount_MapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(ISet<string> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SortedSetCount_MapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(SortedSet<string> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Regression guards — existing behaviour must not break
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RegularReturnStatement_StillCollectsStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample
{
    IEnumerable<string> M(List<string> items)
    {
        return items.Where(x => x.Length > 0);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeachOnArray_NotWrapped()
    {
        var result = Convert(@"
class Sample
{
    void M(string[] arr)
    {
        foreach (var s in arr) { }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Arrays are directly iterable in Java foreach — must NOT be wrapped
        Assert.DoesNotContain("Arrays.asList", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ListCount_StillMapsToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(List<string> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items.size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionBodiedVoidMethod_NotAffected()
    {
        var result = Convert(@"
using System;
class Sample
{
    void M() => Console.WriteLine(""hello"");
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // void method — no collect/asList wrapping
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.asList", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
