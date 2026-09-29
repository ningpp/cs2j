using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that pointer post-increment (*ptr++) inside short-circuit (|| &amp;&amp;)
/// expressions preserves short-circuit semantics.
///
/// Bug: When *ptr++ appears inside a || expression like if (a || *ptr++ &lt; 0xA0),
/// the converter extracts the post-increment as a pre-statement that executes
/// unconditionally before the if, breaking short-circuit semantics. The increment
/// should only happen when earlier || operands evaluate to false.
/// </summary>
public class PointerPostIncrementShortCircuitTests
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
