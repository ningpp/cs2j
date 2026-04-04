using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that lambdas returning Stream where Iterable/Collection is expected
/// automatically add .collect(Collectors.toCollection(() -> new ArrayList<>())).
/// Java's Stream does not implement Iterable, unlike C#'s IEnumerable.
/// </summary>
public class LambdaStreamToIterableTests
{
    [Fact]
    public void Lambda_ReturningStream_WhenTargetIsIterable_AddsCollect()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;
using System.Linq;

class Node { public int Id { get; set; } }

class Test
{
    Func<IEnumerable<int>> _supplier;

    void Init(List<Node> nodes)
    {
        _supplier = () => nodes.Select(n => n.Id);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The lambda body should have .collect() since target returns Iterable
        Assert.Contains(".collect(", result.GeneratedCode);
        Assert.Contains("Collectors.toCollection", result.GeneratedCode);
    }

    [Fact]
    public void Lambda_ReturningNonStream_WhenTargetIsIterable_NoCollect()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;

class Test
{
    Func<IEnumerable<int>> _supplier;

    void Init(List<int> items)
    {
        _supplier = () => items;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Body is just 'items', not a stream pipeline — should NOT add .collect()
        Assert.DoesNotContain("Collectors", result.GeneratedCode);
        Assert.DoesNotContain(".collect(", result.GeneratedCode);
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
