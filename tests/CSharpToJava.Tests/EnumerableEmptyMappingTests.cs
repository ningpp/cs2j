using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Enumerable.Empty&lt;T&gt;() maps to Collections.emptyList() instead of
/// Stream.empty(), because Stream does not implement Iterable in Java and is
/// incompatible with materialized collections in ternary expressions.
/// </summary>
public class EnumerableEmptyMappingTests
{
    [Fact]
    public void EnumerableEmpty_MapsToCollectionsEmptyList()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Test
{
    IEnumerable<string> Get() => Enumerable.Empty<string>();
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Collections.<String>emptyList()", result.GeneratedCode);
        Assert.DoesNotContain("Stream", result.GeneratedCode);
    }

    [Fact]
    public void EnumerableEmpty_InNullCoalescing_CompatibleWithCollection()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Edge { public int Weight { get; set; } }

class Test
{
    List<Edge> _edges;
    IEnumerable<int> GetWeights()
    {
        return _edges?.Select(e => e.Weight) ?? Enumerable.Empty<int>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Empty should use Collections.emptyList, not Stream.empty
        Assert.Contains("emptyList()", result.GeneratedCode);
        Assert.DoesNotContain("Stream.<Integer>empty()", result.GeneratedCode);
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
