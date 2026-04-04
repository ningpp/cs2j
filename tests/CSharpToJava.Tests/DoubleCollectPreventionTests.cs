using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that .collect() is not emitted twice on expressions that are already
/// materialized (e.g., LINQ chain rewritten to stream+collect then passed to ToList).
/// </summary>
public class DoubleCollectPreventionTests
{
    [Fact]
    public void ToList_AfterLinqChain_NoDoubleCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Edge { public int Weight { get; set; } }

class Test
{
    void M(IEnumerable<Edge> edges)
    {
        var weights = edges.Select(e => e.Weight).ToList();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // Should have exactly ONE .collect( call, not two
        var collectCount = CountOccurrences(code, ".collect(");
        Assert.True(collectCount <= 1,
            $"Expected at most 1 .collect() but found {collectCount}:\n{code}");
    }

    [Fact]
    public void Foreach_OnLinqResult_NoDoubleCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Item { public string Name { get; set; } }

class Test
{
    void M(IEnumerable<Item> items)
    {
        var sorted = items.OrderBy(x => x.Name);
        foreach (var item in sorted)
        {
            System.Console.WriteLine(item.Name);
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // The ordered collection should not have double collect
        Assert.DoesNotContain(".collect(Collectors.toCollection(() -> new ArrayList<>())).collect(", code);
    }

    [Fact]
    public void FilterMap_ToList_SingleCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Test
{
    List<string> M(IEnumerable<int> items)
    {
        return items.Where(x => x > 0).Select(x => x.ToString()).ToList();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Output should have at most one .collect()
        var code = result.GeneratedCode;
        Assert.DoesNotContain(").collect(Collectors.toCollection(() -> new ArrayList<>())).collect(", code);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
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
