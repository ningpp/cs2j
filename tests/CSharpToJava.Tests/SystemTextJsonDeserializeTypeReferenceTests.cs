using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Text.Json.JsonSerializer.Deserialize&lt;List&lt;T&gt;&gt;(json, options)
/// keeps the element type via a Jackson TypeReference instead of erasing it to a raw
/// CSharpList.class (which yields LinkedHashMap elements at runtime).
/// </summary>
public class SystemTextJsonDeserializeTypeReferenceTests
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
