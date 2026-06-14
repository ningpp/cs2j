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
    public void UnsafeMethod_RefPointerParam_BaseSegmentUsesHolderValue()
    {
        var result = Convert(@"
unsafe class Test {
    void Encode(ref char* pSrc, char* pSrcEnd, ref char* pDst) {
        int ch = *pSrc;
        *pDst = (char)ch;
        pSrc++;
        pDst++;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("MemorySegment __base1 = pSrc;", result.GeneratedCode);
        Assert.DoesNotContain("MemorySegment __base3 = pDst;", result.GeneratedCode);
        Assert.Contains("MemorySegment __base1 = pSrc.value;", result.GeneratedCode);
        Assert.Contains("MemorySegment __base3 = pDst.value;", result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*\(__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*&\s*0xFF\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_CHAR,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*:\s*'\\0'\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*4\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_INT,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, 0, ((byte)(42)))", result.GeneratedCode);
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
    public void UnsafeMethod_PointerPostIncrementDerefAssignment_AsExpression_ProducesValidJava()
    {
        var result = Convert(@"
unsafe class Test {
    static char EscapedAscii(char first, char second) => first;

    void M(char* dst, char* src) {
        if ((*dst++ = *src++) != '%') {
            return;
        }

        char ch = EscapedAscii((*dst++ = *src++), (*dst++ = *src++));
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain(";)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(";,", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".set(ValueLayout.JAVA_CHAR, 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".get(ValueLayout.JAVA_CHAR, 0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeMethod_PointerPostIncrementDerefAssignment_InGotoStateMachine_EmitsHoistedExpressionsBeforeUse()
    {
        var result = Convert(@"
unsafe class Test {
    static char EscapedAscii(char first, char second) => first;

    void M(char* dst, char* src) {
    again:
        if ((*dst++ = *src++) != '%') goto again;

        char ch = EscapedAscii((*dst++ = *src++), (*dst++ = *src++));
        if (ch == '%') goto again;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode, StringComparison.Ordinal);

        var assignDeclarationIndex = result.GeneratedCode.IndexOf("var _ptrAssign1", StringComparison.Ordinal);
        var assignConditionIndex = result.GeneratedCode.IndexOf("if ((_ptrAssign", StringComparison.Ordinal);
        Assert.True(assignDeclarationIndex >= 0, result.GeneratedCode);
        Assert.True(assignConditionIndex >= 0, result.GeneratedCode);
        Assert.True(assignDeclarationIndex < assignConditionIndex, result.GeneratedCode);

        var localAssignmentIndex = result.GeneratedCode.IndexOf("ch = Test.escapedAscii", StringComparison.Ordinal);
        if (localAssignmentIndex < 0)
        {
            localAssignmentIndex = result.GeneratedCode.IndexOf("ch = escapedAscii", StringComparison.Ordinal);
        }

        var secondArgumentIndex = result.GeneratedCode.IndexOf("var _ptrAssign2", StringComparison.Ordinal);
        var thirdArgumentIndex = result.GeneratedCode.IndexOf("var _ptrAssign3", StringComparison.Ordinal);
        Assert.True(localAssignmentIndex >= 0, result.GeneratedCode);
        Assert.True(secondArgumentIndex >= 0, result.GeneratedCode);
        Assert.True(thirdArgumentIndex >= 0, result.GeneratedCode);
        Assert.True(secondArgumentIndex < localAssignmentIndex, result.GeneratedCode);
        Assert.True(thirdArgumentIndex < localAssignmentIndex, result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(i\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*\(__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(i\)\)\)\s*&\s*0xFF\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(\(long\)\(i\)\s*\*\s*4\)\)\s*\+\s*4\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_INT,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(\(long\)\(i\)\s*\*\s*4\)\)\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, i, ((byte)(42)))", result.GeneratedCode);
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
        Assert.Contains("p.set(ValueLayout.JAVA_INT, (long)(i) * 4", result.GeneratedCode);
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
        // With base segment for pointer params, increment uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*1\)", result.GeneratedCode);
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
        // With base segment for pointer params, increment uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*4\)", result.GeneratedCode);
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
        // With base segment for pointer params, decrement uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*-1\)", result.GeneratedCode);
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
        // With base segment for pointer params, decrement uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*-4\)", result.GeneratedCode);
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
        // With base segment for pointer params, pointer addition uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*n\)", result.GeneratedCode);
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
        // With base segment for pointer params, pointer addition uses base segment approach
        Assert.Matches(@"__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(n\)\s*\*\s*4\)", result.GeneratedCode);
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
        // With base segment, fixed statement generates: __baseN = MemorySegment.ofArray(arr); q = __baseN;
        Assert.Matches(@"MemorySegment __base\d+ = MemorySegment\.ofArray\(arr\)", result.GeneratedCode);
        Assert.Matches(@"MemorySegment q = __base\d+", result.GeneratedCode);
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
        Assert.Contains("MemorySegment.ofArray(new byte[] { ((byte)(tmp)) })", result.GeneratedCode, StringComparison.Ordinal);
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
