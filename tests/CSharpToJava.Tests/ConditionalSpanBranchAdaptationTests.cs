using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A target-typed conditional whose result is ReadOnlySpan&lt;char&gt; must have its
/// string branches wrapped with MemoryExtensions.asSpan(...). C# implicitly converts
/// string to ReadOnlySpan&lt;char&gt;, but Java requires the explicit wrap.
/// Regression: RegexCharClass.SetFromProperty returns
///   !invert ? set : (set[0] == '\0' ? set.AsSpan(1) : NullCharString + set).
/// </summary>
public class ConditionalSpanBranchAdaptationTests
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
