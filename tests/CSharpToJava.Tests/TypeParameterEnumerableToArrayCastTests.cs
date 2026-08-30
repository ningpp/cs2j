using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces ArrayConverter.OnWriteResume's
/// `TElement[] array = (TElement[])(IEnumerable)value;` where value is the
/// TCollection type parameter constrained to IEnumerable. The old output
/// materialized the inner cast as (CSharpGenericIterable&lt;?&gt;)(value),
/// which throws ClassCastException for Java arrays (arrays do not implement
/// CSharpGenericIterable). The array cast goes through TypeHelper.castArray,
/// which accepts the runtime array and boxes primitive elements when needed.
/// </summary>
public class TypeParameterEnumerableToArrayCastTests
{
    [Fact]
    public void TypeParameterConstrainedToEnumerable_CastToArray_NoIterableCast()
    {
        var result = Convert("""
using System.Collections;

class Converter<TCollection, TElement>
    where TCollection : IEnumerable
{
    public TElement[] ToArray(TCollection value)
    {
        return (TElement[])(IEnumerable)value;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The inner (IEnumerable)value cast must not be materialized as a
        // (CSharpGenericIterable<?>)(value) cast: Java arrays cannot be cast to
        // that interface. castArray accepts the runtime array and handles
        // primitive-array covariance that the old (TElement[])(Object) hop could not.
        Assert.DoesNotContain("(CSharpGenericIterable<?>) (value)", code, StringComparison.Ordinal);
        Assert.Contains("TypeHelper.castArray(value, tElementClass)", code, StringComparison.Ordinal);
    }

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
