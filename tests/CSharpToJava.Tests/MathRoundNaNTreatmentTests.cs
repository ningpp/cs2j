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
}
