using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ArrayPool&lt;T&gt;.Shared.Rent/Return map to the compat ArrayPool class.
/// Rent emits a runtime component Class (ArrayPool.rentShared(T.class, n)) so the
/// allocated array is properly typed and the caller's assignment to T[] does not
/// throw ClassCastException at runtime — bare ArrayPool.getShared().rent(n) can only
/// produce Object[] because Java erases the generic type argument. Return keeps the
/// typed-pool form (ArrayPool.&lt;T&gt;getShared().returnValue(...)). Primitive char
/// routes to the shared char[] pool because Java generics cannot express ArrayPool&lt;char&gt;.
/// Reproduces ValueListBuilder/ValueStringBuilder errors.
/// </summary>
public class ArrayPoolTypeWitnessTests
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
