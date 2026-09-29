using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Java forbids creating arrays of generic types (new Pair&lt;T&gt;[] is a compile
/// error). Bare array initializers assigned to a generic-typed array variable must
/// use the raw-type creation plus an unchecked cast, matching new Pair&lt;T&gt;[0].
/// </summary>
public class BareGenericArrayInitializerTests
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
