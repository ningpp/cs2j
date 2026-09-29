using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that dictionary initializer syntax ([key] = value) is correctly
/// converted to Java .put(key, value) calls instead of emitting
/// /* TODO: ImplicitElementAccess */ comments.
/// </summary>
public class ImplicitElementAccessTests
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
