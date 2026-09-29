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
