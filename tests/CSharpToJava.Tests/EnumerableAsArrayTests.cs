using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that "IEnumerable&lt;T&gt; as T[]" produces null in Java because
/// Java arrays do not implement Iterable&lt;T&gt;, making the cast always impossible.
/// </summary>
public class EnumerableAsArrayTests
{
    [Fact]
    public void IEnumerableAsArray_EmitsNull()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Sample {
    int[] M(IEnumerable<int> items) {
        return items as int[] ?? items.ToArray();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // "items as int[]" should become "null" (not instanceof check)
        // because Java arrays don't implement Iterable
        Assert.DoesNotContain("instanceof", result.GeneratedCode);
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
