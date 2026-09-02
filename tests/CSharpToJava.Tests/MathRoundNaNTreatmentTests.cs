using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# Math.Round(NaN) correctly produces NaN in Java.
/// Java's Math.round(double) returns long and returns 0 for NaN,
/// while C#'s Math.Round(double) returns double and returns NaN.
/// </summary>
public class MathRoundNaNTreatmentTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void MathRound_NaNInput_ShouldPreserveNaN()
    {
        var result = Convert(@"
using System;

class Sample
{
    public static double RoundValue(double value)
    {
        return Math.Round(value);
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The generated code should handle NaN: when value is NaN,
        // Math.round(NaN) returns 0 in Java (not NaN as in C#).
        // The converter must preserve NaN semantics.
        Assert.Contains("isNaN", code, StringComparison.Ordinal);
    }
}
