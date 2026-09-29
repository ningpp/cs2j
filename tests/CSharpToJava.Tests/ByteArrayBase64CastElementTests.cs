using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Large byte[] initializers (>= 512 elements) are Base64-encoded at conversion
/// time, which requires evaluating every element to a concrete byte. Real-world
/// initializers contain cast expressions such as (byte)0xBf or (byte)200 (from
/// generated Unicode range tables); the converter must evaluate them instead of
/// parsing the cast text as a number.
/// </summary>
public class ByteArrayBase64CastElementTests
{

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
