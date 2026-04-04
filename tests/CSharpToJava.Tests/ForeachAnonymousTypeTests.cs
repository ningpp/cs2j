using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that foreach loop variables over LINQ queries with anonymous type
/// select clauses are typed correctly (synthesized record), not as Object.
///
/// Root cause: TransformForEachStatement resolved the variable type via
/// MapType BEFORE transforming the collection expression. The anonymous
/// type record was not yet synthesized, so MapType fell back to Object.
/// </summary>
public class ForeachAnonymousTypeTests
{
    [Fact]
    public void ForeachOverLinqSelectAnonymousType_ShouldNotBeObject()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Vertex { public int X { get; set; } public int Y { get; set; } }

class PathFinder
{
    void ProcessPairs(IEnumerable<Vertex> sources, IEnumerable<Vertex> targets)
    {
        foreach (var pair in
            from Vertex s in sources
            from Vertex t in targets
            select new { sourceV = s, targetV = t })
        {
            var s = pair.sourceV;
            var t = pair.targetV;
        }
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode;

        // The foreach variable must NOT be typed as Object
        Assert.DoesNotContain("Object pair", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(Object)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeachOverMethodChainSelectAnonymousType_ShouldNotBeObject()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Item { public string Name { get; set; } public int Value { get; set; } }

class Processor
{
    void Run(List<Item> items)
    {
        foreach (var x in items.Select(i => new { lower = i.Name.ToLower(), doubled = i.Value * 2 }))
        {
            var n = x.lower;
            var v = x.doubled;
        }
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode;

        // The foreach variable must NOT be typed as Object
        Assert.DoesNotContain("Object x", code, StringComparison.Ordinal);
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
