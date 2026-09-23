using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Runtime.CompilerServices.Unsafe.As&lt;int, T&gt;(ref int value) where "T : Enum"
/// must not be emitted as "(T)(Object)(Integer)(...)": javac compiles the (T) cast to a
/// checkcast against T's erasure (java.lang.Enum), which throws ClassCastException for
/// the boxed primitive. Regression: EnumConverter&lt;T&gt;.Read returned (T) from JSON
/// numbers — every enum property deserialization failed with Integer → Enum CCE.
/// The generic Unsafe.as(Object) overload erases to Object, so no checkcast is emitted.
/// </summary>
public class UnsafeAsEnumTypeParameterTests
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
