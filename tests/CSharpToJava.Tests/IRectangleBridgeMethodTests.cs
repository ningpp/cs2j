using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Iteration 2 regression from the MSAGL test suite:
/// IRectangle&lt;P&gt;.Add(P) returns void in C#. The class implements the interface
/// with primitive-typed methods (void add(double)), so the converter must emit a
/// void boxed bridge (void add(Double) → add((double) point)) — not one returning
/// IRectangle&lt;Double&gt;, which would not implement the void interface method.
/// Regression: Interval.java → "Interval中的add(java.lang.Double)无法实现add(P)".
/// </summary>
public class IRectangleBridgeMethodTests
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
