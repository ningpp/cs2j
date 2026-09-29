using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that (int)ptr / (long)ptr casts only append .address() when the
/// operand is a real MemorySegment pointer. Pointer casts of integral values
/// (IntPtr → long, numeric literals) and Unsafe.AsPointer(...) results are
/// already raw numeric addresses in Java — appending .address() produces
/// invalid Java ("cannot dereference int/long") like
/// (int)(Unsafe.asPointer(searchSpace).address()) or (int)(index.address()).
/// Reproduces JsonReaderHelper.IndexOfOrLessThan javac errors.
/// </summary>
public class PointerCastToIntegralTests
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
