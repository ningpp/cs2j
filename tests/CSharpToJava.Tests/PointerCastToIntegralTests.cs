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
    [Fact]
    public void CastUnsafeAsPointerToInt_DoesNotAppendAddress()
    {
        var result = Convert(@"
using System;
using System.Runtime.CompilerServices;
unsafe class Holder {
    private static int Foo(ref byte searchSpace) {
        return (int)Unsafe.AsPointer(ref searchSpace) & 15;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(int)(Unsafe.asPointer(searchSpace))", code);
        Assert.DoesNotContain("asPointer(searchSpace).address()", code);
    }

    [Fact]
    public void CastIntPtrToIntPointerChain_DoesNotAppendAddress()
    {
        var result = Convert(@"
using System;
unsafe class Holder {
    private static int Foo(int length) {
        IntPtr index = (IntPtr)0;
        int b = (int)(byte*)index;
        return b;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(int)(index)", code);
        Assert.DoesNotContain("index.address()", code);
    }

    [Fact]
    public void CastRealPointerToInt_StillAppendsAddress()
    {
        var result = Convert(@"
unsafe class Holder {
    private static int Foo(byte* p) {
        return (int)p;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(int)(p.address())", code);
    }

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
