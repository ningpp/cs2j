using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Type.IsAssignableFrom must route through TypeHelper.isAssignableFrom so
/// Java arrays (which do not implement CSharpGenericIterable) still satisfy the
/// C# "IEnumerable.IsAssignableFrom(array)" check used by
/// IEnumerableConverterFactory.CanConvert.
/// </summary>
public class TypeIsAssignableFromTests
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
