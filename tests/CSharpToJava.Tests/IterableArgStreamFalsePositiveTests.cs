using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a method returning IEnumerable&lt;T&gt; is passed as a constructor argument,
/// its result is NOT incorrectly treated as a stream expression just because its *inner* arguments
/// contain stream operations (like .map() or .filter()). 
/// Regression: gluedPolyline(poly.Select(...).ToArray(), map) was getting
/// ".collect(Collectors.toCollection(...))" appended because the inner .map() triggered
/// LooksLikeJavaStreamExpression on the entire expression string.
/// </summary>
public class IterableArgStreamFalsePositiveTests
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
