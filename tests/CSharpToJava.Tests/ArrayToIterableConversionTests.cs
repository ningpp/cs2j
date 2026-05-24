using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that arrays are properly wrapped when passed to methods expecting Iterable/Collection,
/// or returned from methods returning IEnumerable/Iterable.
/// In Java, arrays don't implement Iterable, unlike C# where arrays implement IEnumerable.
/// </summary>
public class ArrayToIterableConversionTests
{
    /// <summary>
    /// Reproduces: "Node[] cannot be converted to Iterable&lt;Node&gt;"
    /// when a property backed by an array returns IEnumerable&lt;T&gt;.
    /// </summary>
    [Fact]
    public void Property_ReturningArray_AsIEnumerable_WrapsWithArraysAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Node { }
class Constraint
{
    Node[] V;
    public IEnumerable<Node> Nodes { get { return V; } }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ArrayHelper.asListView(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduces: "Cluster[] cannot be converted to Iterable&lt;Cluster&gt;"
    /// when passing new T[] as constructor argument expecting IEnumerable&lt;T&gt;.
    /// </summary>
    [Fact]
    public void ConstructorArg_ArrayLiteral_ToIEnumerable_Wraps()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Cluster { }
class Layout
{
    IEnumerable<Cluster> clusters;
    public Layout(IEnumerable<Cluster> items) { clusters = items; }
    public Layout(Cluster single)
        : this(new Cluster[] { single })
    {
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The array literal should be wrapped when passed to IEnumerable parameter
        Assert.Contains("ArrayHelper.toList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Method returning array field as IEnumerable should wrap with a live array view.
    /// </summary>
    [Fact]
    public void Method_ReturningArrayField_AsIEnumerable_Wraps()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Node { }
class Graph
{
    Node[] nodes;
    public IEnumerable<Node> GetNodes() { return nodes; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ArrayHelper.asListView(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduces the MSAGL failure mode: a property backed by an array and exposed
    /// as IList<T> must return a live view, so indexed writes mutate the same array.
    /// </summary>
    [Fact]
    public void Property_ReturningArray_AsIList_UsesLiveView()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Node { }
class Graph
{
    Node[] nodes;
    public IList<Node> Nodes { get { return nodes; } set { nodes = value as Node[]; } }
    void M()
    {
        Nodes = new Node[2];
        Nodes[0] = new Node();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ArrayHelper.asListView(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.toList(nodes)", result.GeneratedCode, StringComparison.Ordinal);
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
