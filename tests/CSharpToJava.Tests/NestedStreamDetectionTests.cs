using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that stream detection only considers top-level stream methods, not stream
/// calls nested inside arguments of non-stream methods. Prevents false-positive
/// .collect() being added to method calls like createComponents(values().stream()...).
/// </summary>
public class NestedStreamDetectionTests
{
    [Fact]
    public void ReturnStatement_MethodCallWithStreamInArgs_NoCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Component { }

static class GraphHelper
{
    public static IEnumerable<Component> CreateComponents(IList<string> names, int count)
    {
        return new List<Component>();
    }
}

class Test
{
    IEnumerable<Component> Build(List<string> items, int n)
    {
        // Stream is inside argument (items.Select -> stream), not at top level
        return GraphHelper.CreateComponents(items.Select(x => x.ToUpper()).ToList(), n);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // The return value of createComponents should NOT have .collect() appended
        // because createComponents() is not a stream method — the stream is only inside its arguments
        int returnIdx = code.IndexOf("return ");
        Assert.True(returnIdx >= 0, "Expected a return statement in generated code");
        var returnLine = code[returnIdx..code.IndexOf(";", returnIdx)];
        Assert.DoesNotContain(".collect(Collectors", returnLine);
    }

    [Fact]
    public void ForEach_AlreadyCollectedVariable_NoDoubleCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Item { public int Value { get; set; } }

class Test
{
    void Process(List<Item> items)
    {
        var sorted = items.OrderBy(x => x.Value);
        foreach (var item in sorted)
        {
            System.Console.WriteLine(item.Value);
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // Count .collect( occurrences — should be at most 1
        int collectCount = 0;
        int idx = 0;
        while ((idx = code.IndexOf(".collect(", idx)) != -1) { collectCount++; idx += 9; }
        Assert.True(collectCount <= 1,
            $"Expected at most 1 .collect() but found {collectCount}:\n{code}");
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
