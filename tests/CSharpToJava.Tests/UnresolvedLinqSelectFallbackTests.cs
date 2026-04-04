using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ .Select() that wasn't rewritten by the LINQ rewriter
/// (e.g., in null-conditional context) still gets mapped to a Java stream pipeline.
/// </summary>
public class UnresolvedLinqSelectFallbackTests
{
    [Fact]
    public void Select_OnIEnumerable_MappedToStreamMap()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Edge { public int Weight { get; set; } }

class Test
{
    IEnumerable<Edge> _edges;
    IEnumerable<int> GetWeights()
    {
        return _edges.Select(e => e.Weight);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Select(", result.GeneratedCode);
        // Should use stream().map() or StreamSupport.stream()...map()
        Assert.Contains(".map(", result.GeneratedCode);
    }

    [Fact]
    public void Select_InNullCoalescing_MappedToStreamMap()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Edge { public int Weight { get; set; } }

class Test
{
    IEnumerable<Edge> _edges;
    IEnumerable<int> GetWeights()
    {
        return _edges?.Select(e => e.Weight) ?? Enumerable.Empty<int>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Select(", result.GeneratedCode);
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
