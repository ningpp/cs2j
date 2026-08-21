using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class JaggedArrayDebugOutputTests
{
    [Fact]
    public void Output_GeneratedCode_For_JaggedArray()
    {
        var source = @"
using System;
using System.Linq;

class Sample {
    static double[][] M(double[][] source) {
        var a = source.Select(row => (double[])row.Clone()).ToArray();
        return a;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Output the code for debugging
        var code = result.GeneratedCode;
        // Check if "new double[" appears without "[][" (the bug)
        // The correct pattern should be "new double[...][]"
        Assert.Contains("ProceduralLinq", code);
        // Should have "new double[" followed by "][" for the outer array
        Assert.Contains("new double[", code);
        // Should contain "][]" for the jagged array creation
        Assert.Contains("][]", code);
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
