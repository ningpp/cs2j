using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for Class token placement when a generic method
/// body uses typeof(T) or default(T). The converter adds a leading
/// Class&lt;T&gt; _cs2j_T parameter to the method signature (via
/// AddPendingRuntimeClassParameters). Call sites must PREPEND the
/// Class literal to match the leading parameter position.
/// </summary>
public class JsonSerializerDeserializeClassTokenOrderTests
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
