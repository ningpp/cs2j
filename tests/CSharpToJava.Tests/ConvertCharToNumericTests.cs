using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Convert.ToXxx(char) preserves C# char-to-numeric semantics
/// (Unicode code point) instead of being incorrectly mapped to Java parse methods.
/// </summary>
public class ConvertCharToNumericTests
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
