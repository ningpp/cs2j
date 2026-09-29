using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Static, non-capturing local functions declared after a return statement must be
/// hoisted to class-level private methods (like other local functions).
/// Regression: RegexCompiler.NodeSupportsNonBacktrackingImplementation was dropped,
/// leaving a call to nodeSupportsNonBacktrackingImplementation without a declaration.
/// </summary>
public class LocalFunctionAfterReturnHoistingTests
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
