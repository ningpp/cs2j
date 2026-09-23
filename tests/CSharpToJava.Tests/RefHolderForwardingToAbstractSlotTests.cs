using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that forwarding a ref parameter into a call whose target slot is
/// holder-wrapped (abstract generic base) passes the holder itself, not the
/// unwrapped .value — otherwise the call passes Utf8JsonReader where
/// ObjectHolder&lt;Utf8JsonReader&gt; is expected. Reproduces
/// CharConverter.ReadWithQuotes "Utf8JsonReader cannot be converted to
/// ObjectHolder&lt;Utf8JsonReader&gt;".
/// </summary>
public class RefHolderForwardingToAbstractSlotTests
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
