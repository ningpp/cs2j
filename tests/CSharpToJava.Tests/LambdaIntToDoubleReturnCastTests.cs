using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# lambdas returning int/Integer but targeting a Func<int, double>
/// (mapped to Java Function&lt;Integer, Double&gt;) have their return value cast
/// to double. Java does not auto-widen Integer to Double.
/// </summary>
public class LambdaIntToDoubleReturnCastTests
{
    private readonly ITestOutputHelper _out;

    public LambdaIntToDoubleReturnCastTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void LambdaReturningInt_TargetingDouble_ReturnsCastedValue()
    {
        var result = Convert("""
using System;

class BimodalSequence
{
    public BimodalSequence(Func<int, double> sequence, int length) { }
}

class Test
{
    static void Run()
    {
        var seq = new BimodalSequence(i => i, 5);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The lambda body should be cast to double
        Assert.Contains("(double)", code, StringComparison.Ordinal);
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
