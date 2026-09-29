using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ++/-- on properties/indexers inside expression-bodied void members
/// (where the expression value is discarded) emit the in-place setter rewrite
/// instead of hoisting a temp whose leftover bare statement is invalid Java.
/// Reproduces RegexParser.PopKeepOptions: <c>void PopKeepOptions() => _optionsStack.Length--;</c>
/// </summary>
public class VoidExpressionBodyPropertyIncrementTests
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
