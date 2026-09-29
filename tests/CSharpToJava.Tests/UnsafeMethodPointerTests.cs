using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnsafeMethodPointerTests
{
    [Fact]
    public void UnsafeMethod_BytePointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_CharPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_LongPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(long* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_FloatPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(float* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_DoublePointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(double* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_SBytePointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(sbyte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_ShortPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(short* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BoolPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(bool* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_MultiplePointerParams_AllMapToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void Decode(char* pChars, char* pCharsEndPos, byte* pBytes, byte* pBytesEndPos) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment pChars", result.GeneratedCode);
        Assert.Contains("MemorySegment pCharsEndPos", result.GeneratedCode);
        Assert.Contains("MemorySegment pBytes", result.GeneratedCode);
        Assert.Contains("MemorySegment pBytesEndPos", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_MixedPointerAndNonPointerParams()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p, int count, byte* q) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
        Assert.Contains("int count", result.GeneratedCode);
        Assert.Contains("MemorySegment q", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_PointerParamWithOutParam()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* pBytes, out int bytesDecoded) { bytesDecoded = 0; }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment pBytes", result.GeneratedCode);
        Assert.Contains("IntHolder bytesDecoded", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_PointerParamWithRefParam()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char* pChars, ref int count) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment pChars", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_NoPointerParams_NoScopeInjection()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int x) { }
}");
        Assert.True(result.Success);
        Assert.Contains("int x", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_UnsafeModifierStripped()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_DecodeLikeMethod()
    {
        var result = Convert(@"
unsafe class Test {
    private unsafe void Decode(char* pChars, char* pCharsEndPos,
                               byte* pBytes, byte* pBytesEndPos,
                               out int charsDecoded, out int bytesDecoded)
    {
        charsDecoded = 0;
        bytesDecoded = 0;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment pChars", result.GeneratedCode);
        Assert.Contains("MemorySegment pCharsEndPos", result.GeneratedCode);
        Assert.Contains("MemorySegment pBytes", result.GeneratedCode);
        Assert.Contains("MemorySegment pBytesEndPos", result.GeneratedCode);
        Assert.Contains("IntHolder charsDecoded", result.GeneratedCode);
        Assert.Contains("IntHolder bytesDecoded", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_VoidPointerParam_MapsToMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(void* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_StaticUnsafeMethod()
    {
        var result = Convert(@"
unsafe class Test {
    static unsafe void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("static", result.GeneratedCode);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
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
