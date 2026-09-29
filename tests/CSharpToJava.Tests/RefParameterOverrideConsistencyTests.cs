using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A ref parameter on an internal virtual method must render as the same Java
/// holder type on the base declaration and on every override. JsonConverter&lt;T&gt;
/// generated OnTryWrite(WriteStack)/OnTryRead(ReadStack) without the holder while
/// ObjectDefaultConverter etc. overrode with ObjectHolder, silently disabling the
/// overrides and causing JsonResumableConverter read/write self-recursion.
/// </summary>
public class RefParameterOverrideConsistencyTests
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
