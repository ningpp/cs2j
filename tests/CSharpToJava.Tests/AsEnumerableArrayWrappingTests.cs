using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that AsEnumerable() on arrays is not simply stripped but wrapped with Arrays.asList(),
/// since Java arrays don't implement Iterable (unlike C# where arrays implement IEnumerable).
/// Reproduces: ProcrustesCircleConstraint.java — Node[] cannot be converted to Iterable&lt;Node&gt;
/// </summary>
public class AsEnumerableArrayWrappingTests
{
    /// <summary>
    /// Property-style: return V.AsEnumerable() where V is T[] and return type is IEnumerable&lt;T&gt;.
    /// After stripping AsEnumerable, V should be wrapped with Arrays.asList(V).
    /// </summary>
    [Fact]
    public void AsEnumerable_OnArrayField_InReturn_WrapsWithArraysAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node { }
class Constraint
{
    Node[] V;
    public IEnumerable<Node> Nodes { get { return V.AsEnumerable(); } }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Arrays.asList(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AsEnumerable", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Method returning array.AsEnumerable() should also wrap with Arrays.asList().
    /// </summary>
    [Fact]
    public void AsEnumerable_OnArrayField_InMethod_WrapsWithArraysAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node { }
class Graph
{
    Node[] nodes;
    public IEnumerable<Node> GetNodes() { return nodes.AsEnumerable(); }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Arrays.asList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// AsEnumerable on a non-array Iterable should still be stripped (no wrapping needed).
    /// </summary>
    [Fact]
    public void AsEnumerable_OnList_IsStrippedWithoutWrapping()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node { }
class Graph
{
    List<Node> nodes = new List<Node>();
    public IEnumerable<Node> GetNodes() { return nodes.AsEnumerable(); }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("AsEnumerable", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.asList(", result.GeneratedCode, StringComparison.Ordinal);
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
