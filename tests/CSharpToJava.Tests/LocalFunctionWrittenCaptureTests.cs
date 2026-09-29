using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that captured variables WRITTEN inside a hoisted local function keep C#
/// by-reference capture semantics (holder parameter + write-back at call sites),
/// instead of silently losing the mutation on a by-value parameter.
/// </summary>
public class LocalFunctionWrittenCaptureTests
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
