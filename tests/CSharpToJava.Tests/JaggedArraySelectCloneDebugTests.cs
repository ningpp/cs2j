using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Debug test to inspect the actual generated code for jagged array
/// Select+Clone+ToArray pattern from the MSAGL codebase.
/// </summary>
public class JaggedArraySelectCloneDebugTests
{
    [Fact]
    public void Debug_DoubleJaggedArray_SelectClone_ToArray()
    {
        var source = @"
using System;
using System.Linq;

class Sample {
    static void M(double[][] source) {
        var a = source.Select(row => (double[])row.Clone()).ToArray();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Print the generated code for inspection
        Assert.NotNull(result.GeneratedCode);
    }

    [Fact]
    public void Debug_DoubleJaggedArray_SelectClone_ToArray_WithLocalFunction()
    {
        // This mirrors the actual MSAGL pattern where the Select+ToArray
        // is inside a method with double[][] parameter and return type
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
        // The generated Java code should contain "new double[" followed by "]["
        // to create a double[][] array, not just "new double[" which creates double[]
        Assert.Contains("][]", result.GeneratedCode);
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
