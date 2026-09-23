using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the GroupCollection javac failure:
/// CSharpReadOnlyDict&lt;K, V&gt; now declares containsKey(K)/get(K), so the
/// converter must NOT also emit containsKey(Object)/get(Object) bridges for a
/// concrete key type such as String. Those bridges have the same erasure as the
/// typed members and javac reports a name clash.
/// </summary>
public class ReadOnlyDictErasureClashTests
{

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
