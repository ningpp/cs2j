using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Array.GetValue(index) on a concrete array type is translated
/// to Java array indexing, because the receiver maps to a plain Java array.
/// </summary>
public class ArrayGetValueTests
{

    [Fact]
    public void SystemArray_GetValue_KeepsWrapperMethod()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public object GetItem(Array arr, int index)
    {
        return arr.GetValue(index);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(".getValue(", result.GeneratedCode, StringComparison.Ordinal);
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
