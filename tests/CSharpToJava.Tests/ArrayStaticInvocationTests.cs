using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayStaticInvocationTests
{
    [Fact]
    public void ArraySort_WithIndexAndLength_UsesArraysSortRange()
    {
        var result = Convert(@"
using System;

class Test {
    void M(int[] values) {
        Array.Sort(values, 1, 2);
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("Arrays.sort(values, 1, 1 + 2)", result.GeneratedCode);
        Assert.DoesNotContain("Object.sort", result.GeneratedCode);
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
