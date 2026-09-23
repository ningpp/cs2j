using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# reflection metadata uses Type.MakeGenericMethodParameter(0) and
/// typeof(X&lt;&gt;).MakeGenericType(T). These were emitted as
/// Class.makeGenericMethodParameter(0) and X.class.makeGenericType(...), neither of
/// which exists on java.lang.Class. Since Class.getMethod matches by erasure, the
/// converter must emit the erased forms: Object.class for the method type parameter
/// and the raw class literal for MakeGenericType.
/// Regression: RegexCompiler's s_spanIndexOf/s_spanStartsWith reflection stubs.
/// </summary>
public class TypeReflectionMetadataErasureTests
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
