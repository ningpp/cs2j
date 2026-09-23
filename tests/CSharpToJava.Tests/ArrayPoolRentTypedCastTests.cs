using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ArrayPool&lt;T&gt;.Shared.Rent results are cast to the typed
/// element array. rentShared(T.class, n) infers the raw element type (or
/// Object), so `var newCache = ...; typedArray = newCache;` fails with
/// "Object[] cannot be converted to Tuple5<...>[]". Reproduces
/// ObjectWithParameterizedConstructorConverter.Grow.
/// </summary>
public class ArrayPoolRentTypedCastTests
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
