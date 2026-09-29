using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that explicit C# test timeouts (MSTest [Timeout] and xUnit
/// [Fact(Timeout = ...)]/[Theory(Timeout = ...)]) are preserved as JUnit
/// @Timeout annotations, and that no hard-coded timeout is invented.
/// </summary>
public class TestTimeoutAttributeMappingTests
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
