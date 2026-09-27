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
