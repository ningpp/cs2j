using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# primitive numeric CompareTo calls map to the correct Java
/// static comparison helper. double.CompareTo(double) must become
/// Double.compare(double, double), not Integer.compare(int, int).
/// </summary>
public class PrimitiveCompareToMappingTests
{

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
