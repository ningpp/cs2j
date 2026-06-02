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
    public void UnsafeMethod_PointerParam_ImportsAdded()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) { }
}");
        Assert.True(result.Success);
        Assert.Contains("import java.lang.foreign.MemorySegment", result.GeneratedCode);
        Assert.Contains("import java.lang.foreign.ValueLayout", result.GeneratedCode);
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
    public void UnsafeMethod_BytePointerDerefRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) {
        int v = *p;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0) & 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_SBytePointerDerefRead_NoMask()
    {
        var result = Convert(@"
unsafe class Test {
    void M(sbyte* p) {
        byte v = (byte)*p;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_CharPointerDerefRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char* p) {
        char v = *p;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_CHAR, 0)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerDerefRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p) {
        int v = *p;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_INT, 0)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerDerefWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) {
        *p = 42;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, 0, (byte) 42)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_CharPointerDerefWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char* p) {
        *p = 'a';
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_CHAR, 0", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerIndexRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p, int i) {
        int v = p[i];
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, i) & 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerIndexRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p, int i) {
        int v = p[i];
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_INT, (long)i * 4)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerIndexWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p, int i) {
        p[i] = 42;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, i, (byte) 42)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerIndexWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p, int i) {
        p[i] = 42;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_INT, (long)i * 4", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerIncrement()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) {
        p++;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p = p.asSlice(1)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerIncrement()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p) {
        p++;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p = p.asSlice(4)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerDecrement()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p) {
        p--;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p = p.asSlice(-1)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerDecrement()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p) {
        p--;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p = p.asSlice(-4)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_BytePointerAddOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p, int n) {
        byte* q = p + n;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.asSlice(n)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeMethod_IntPointerAddOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int* p, int n) {
        int* q = p + n;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.asSlice((long)n * 4)", result.GeneratedCode);
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
    public void UnsafeMethod_PointerParamWithFixedStatementInside()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte* p, byte[] arr) {
        fixed (byte* q = arr) {
            *p = *q;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p", result.GeneratedCode);
        Assert.Contains("MemorySegment q = MemorySegment.ofArray(arr)", result.GeneratedCode);
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

    [Fact]
    public void UnsafeMethod_AddressOfIntLocal_AsPointerArgument_UsesMemorySegmentScratch()
    {
        var result = Convert(@"
unsafe class Test {
    void Fill(int* p) {
        *p = 42;
    }

    void M() {
        int tmp = 0;
        Fill(&tmp);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment _addr_tmp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MemorySegment.ofArray(new int[] { tmp })", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("fill(_addr_tmp)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("C# addressof", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeMethod_AddressOfByteLocal_AsPointerArgument_UsesByteArrayScratch()
    {
        var result = Convert(@"
unsafe class Test {
    void Fill(byte* p) {
        *p = 255;
    }

    void M() {
        byte tmp = 0;
        Fill(&tmp);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment _addr_tmp", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MemorySegment.ofArray(new byte[] { (byte) tmp })", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("fill(_addr_tmp)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("C# addressof", result.GeneratedCode, StringComparison.Ordinal);
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
