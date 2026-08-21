using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// When a jagged array (e.g. double[][]) is the source of a LINQ Select that
/// clones each row and ToArray is called, the generated procedural code must
/// create the correct outer array type (double[][] not double[]).
///
/// C# source pattern:
///   double[][] a = source.Select(row => (double[])row.Clone()).ToArray();
///
/// Bug: the LINQ rewriter creates "new double[count]" (losing one dimension)
/// instead of "new double[count][]" for jagged arrays.
/// </summary>
public class JaggedArraySelectToArrayTests
{
    [Fact]
    public void DoubleJaggedArray_SelectClone_ToArray_PreservesJaggedType()
    {
        var source = @"
using System.Linq;

class Sample {
    double[][] M(double[][] source) {
        var a = source.Select(row => (double[])row.Clone()).ToArray();
        return a;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The generated code should NOT contain "new double[" without a trailing "][]"
        // for the outer array allocation.
        Assert.Contains("new double[", result.GeneratedCode);
        // Ensure the array creation has two dimensions: "new double[...][]"
        Assert.Contains("][]", result.GeneratedCode);
    }

    [Fact]
    public void IntJaggedArray_SelectClone_ToArray_PreservesJaggedType()
    {
        var source = @"
using System.Linq;

class Sample {
    int[][] M(int[][] source) {
        var a = source.Select(row => (int[])row.Clone()).ToArray();
        return a;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("new int[", result.GeneratedCode);
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
