using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that (T[])(IEnumerable)x casts compile: the source is an enumerable
/// interface (not a Java Collection subtype), so a plain (T[]) cast on the
/// CSharpGenericIterable<?> mapping is rejected by javac. The C# cast is a
/// reference cast (the runtime object IS a TElement[]), so the converter emits
/// TypeHelper.castArray which accepts the runtime array and boxes primitive
/// elements when needed. Reproduces ArrayConverter.OnWriteResume
/// "incompatible types: CSharpGenericIterable cannot be converted to TElement[]".
/// </summary>
public class EnumerableInterfaceToArrayCastTests
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
