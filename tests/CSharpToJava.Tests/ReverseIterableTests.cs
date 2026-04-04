using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that .Reverse() returns a List (Iterable-compatible) when not chained,
/// and returns a Stream when chained into further LINQ operations.
/// </summary>
public class ReverseIterableTests
{
    [Fact]
    public void Reverse_PassedAsArgument_ReturnsList()
    {
        // When .Reverse() result is used as a method argument (expecting IEnumerable<T>),
        // it should return list, not list.stream(), so it's compatible with Iterable<T>.
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M(IEnumerable<int> items) {
        Process(items.Reverse());
    }
    void Process(IEnumerable<int> data) { }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Should return list (not list.stream()) so it's Iterable-compatible
        Assert.Contains("return list; }", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return list.stream();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Reverse_ChainedWithToList_ReturnsStream()
    {
        // When .Reverse() is chained with .ToList(), it should return list.stream()
        // so the downstream .collect() can operate on a stream.
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M() {
        var items = new List<string> { ""a"", ""b"" };
        var reversed = items.Reverse<string>().ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("return list.stream();", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
