using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that non-generic System.Collections.ICollection parameters accept generic
/// collection arguments (List&lt;T&gt;, etc.) in the generated Java.
/// In C#, List&lt;T&gt; implements both ICollection&lt;T&gt; (generic) and ICollection
/// (non-generic). But in the Java compat library, CSharpList&lt;T&gt; only implements
/// CSharpICollection&lt;T&gt;, not the raw CSharpCollection interface. So a non-generic
/// ICollection parameter must be emitted as CSharpICollection&lt;?&gt; to accept
/// CSharpList&lt;T&gt; arguments.
/// </summary>
public class NonGenericICollectionParameterTests
{
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

    /// <summary>
    /// Reproduces MSAGL EdgeLabelPlacement.java:190 compile error:
    /// "CSharpList&lt;CSharpKeyValuePair&lt;Double,Point&gt;&gt; cannot be converted to CSharpCollection"
    /// when a List&lt;KeyValuePair&lt;double, Point&gt;&gt; is passed to a method whose
    /// parameter is non-generic ICollection.
    /// </summary>
    [Fact]
    public void ListOfKeyValuePairs_PassedToNonGenericICollectionParam_Compiles()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class Point { }
class Label { public double PlacementOffset; }
class Edge { }

class EdgeLabelPlacement
{
    Dictionary<Edge, List<KeyValuePair<double, Point>>> edgePoints = new();

    void PlaceLabel(Label label, Edge edge)
    {
        List<KeyValuePair<double, Point>> points = edgePoints[edge];
        int index = StartIndex(label, points);
    }

    static int StartIndex(Label label, ICollection points)
    {
        return System.Math.Min(points.Count - 1, System.Math.Max(0, (int)System.Math.Floor(points.Count * label.PlacementOffset)));
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The StartIndex parameter must be emitted as CSharpICollection<?> (not raw CSharpCollection)
        // so that CSharpList<CSharpKeyValuePair<...>> is assignable to it.
        Assert.Contains("CSharpICollection<?>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpCollection points)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// A simpler reproduction: List&lt;int&gt; passed to a non-generic ICollection parameter.
    /// </summary>
    [Fact]
    public void ListOfInt_PassedToNonGenericICollectionParam_UsesWildcardGeneric()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class Sample
{
    int Read(List<int> items)
    {
        return CountItems(items);
    }

    static int CountItems(ICollection items)
    {
        return items.Count;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpICollection<?>", result.GeneratedCode, StringComparison.Ordinal);
    }
}
