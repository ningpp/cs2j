using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces ArrayConverter.OnWriteResume's
/// `TElement[] array = (TElement[])(IEnumerable)value;` where value is the
/// TCollection type parameter constrained to IEnumerable. The old output
/// materialized the inner cast as (CSharpGenericIterable&lt;?&gt;)(value),
/// which throws ClassCastException for Java arrays (arrays do not implement
/// CSharpGenericIterable). The cast must stay an identity hop through Object.
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
        // that interface. The array cast stays an identity hop through Object.
        Assert.DoesNotContain("(CSharpGenericIterable<?>) (value)", code, StringComparison.Ordinal);
        Assert.Contains("(TElement[]) (Object) (value)", code, StringComparison.Ordinal);
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
