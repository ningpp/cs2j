using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ReadOnlyMemory&lt;byte&gt;.ToArray() maps to the compat
/// ReadOnlyMemory.toByteArray() (byte[] result), and that the implicit
/// byte[] → ReadOnlyMemory&lt;byte&gt; conversion wraps the array in
/// new ReadOnlyMemory&lt;Integer&gt;(byte[]). The compat library must provide
/// both APIs for the generated code to compile.
/// </summary>
public class ReadOnlyMemoryToByteArrayTests
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
