using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# unboxing casts from object to integral types.
/// </summary>
public class EnumCastFromObjectTests
{
    private static Core.Pipeline.ConversionResult Convert(string sourceCode)
    {
        var pipeline = new Core.Pipeline.ConversionPipeline();
        return pipeline.Convert(new Core.Pipeline.ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new Core.Context.ConversionOptions(),
        });
    }
}
