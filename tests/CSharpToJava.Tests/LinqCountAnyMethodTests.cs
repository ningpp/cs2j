using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LinqCountAnyMethodTests
{
    [Fact]
    public void Count_OnIEnumerable_EmittedAsSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    int M(IEnumerable<int> items) {
        return items.Count();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Count() on IEnumerable should not emit bare .count() (doesn't exist on Iterable)
        Assert.DoesNotContain(".count()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_OnIEnumerable_EmittedCorrectly()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test {
    bool M(IEnumerable<int> items) {
        return items.Any();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Any() on IEnumerable should not emit bare .any() (doesn't exist on Iterable)
        Assert.DoesNotContain(".any()", result.GeneratedCode, StringComparison.Ordinal);
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
