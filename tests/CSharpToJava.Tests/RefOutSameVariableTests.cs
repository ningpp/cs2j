using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ref holder initialization correctly uses the current value
/// when the same variable was previously assigned via an out parameter.
/// </summary>
public class RefOutSameVariableTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
