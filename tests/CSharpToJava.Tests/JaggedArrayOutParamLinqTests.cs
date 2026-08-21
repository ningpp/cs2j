using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that the actual MSAGL pattern (SymmetricEigenJacobi with out parameters
/// and double[][] source) generates correct jagged array creation in the LINQ
/// procedural helper method.
/// </summary>
public class JaggedArrayOutParamLinqTests
{
    [Fact]
    public void OutParamMethod_WithJaggedArraySelectClone_GeneratesCorrectArrayType()
    {
        // Mirrors the MSAGL SymmetricEigenJacobi pattern:
        // static void M(double[][] source, out double[] ev, out double[][] evec)
        // with source.Select(row => (double[])row.Clone()).ToArray() inside
        var source = @"
using System;
using System.Linq;

class Sample {
    static void M(double[][] source, out double[] ev, out double[][] evec) {
        int n = source.Length;
        var a = source.Select(row => (double[])row.Clone()).ToArray();
        evec = new double[n][];
        ev = new double[n];
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The generated procedural helper should create double[][] not double[]
        // Look for the pattern "new double[" without "][]" — that's the bug
        var code = result.GeneratedCode;
        // Find any _ProceduralLinq helper — it should have "new double[" followed by "]["
        // If we find "new double[" NOT followed by "][", that's the bug
        Assert.Contains("ProceduralLinq", code);
        // The array creation should have two dimensions
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
