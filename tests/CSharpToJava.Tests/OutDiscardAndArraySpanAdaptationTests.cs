using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests two gaps exposed by HexConverter:
/// 1. out-discard arguments (out _) must use the typed holder (IntHolder for
///    out int), not new Object[1], so the typed overload accepts them.
/// 2. A byte[] produced where a ReadOnlySpan&lt;byte&gt; is expected (expression
///    body / return) must be wrapped in new ReadOnlySpan&lt;&gt;(...).
/// </summary>
public class OutDiscardAndArraySpanAdaptationTests
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
