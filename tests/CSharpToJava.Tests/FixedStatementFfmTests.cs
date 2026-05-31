using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FixedStatementFfmTests
{
    [Fact]
    public void FixedBytePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedSBytePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(sbyte[] arr) {
        fixed (sbyte* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedShortPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(short[] arr) {
        fixed (short* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedIntPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedLongPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(long[] arr) {
        fixed (long* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedFloatPointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(float[] arr) {
        fixed (float* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedDoublePointer_Array()
    {
        var result = Convert(@"
unsafe class Test {
    void M(double[] arr) {
        fixed (double* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_DerefRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte v = *p;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0) & 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_DerefWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            *p = 42;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, 0, (byte) 42)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_IndexRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr, int i) {
        fixed (byte* p = arr) {
            byte v = p[i];
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, i) & 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_IndexWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr, int i) {
        fixed (byte* p = arr) {
            p[i] = 42;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, i, (byte) 42)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_ArithmeticResult()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte v = (byte)(p[0] + p[1]);
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("& 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void FixedSBytePointer_NoMask()
    {
        var result = Convert(@"
unsafe class Test {
    void M(sbyte[] arr) {
        fixed (sbyte* p = arr) {
            sbyte v = *p;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_BYTE, 0)", result.GeneratedCode);
        Assert.DoesNotContain("& 0xFF", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_DerefRead()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            char v = *p;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_CHAR, 0)", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_DerefWrite()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            *p = 'a';
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.set(ValueLayout.JAVA_CHAR, 0, 'a')", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_FromString()
    {
        var result = Convert(@"
unsafe class Test {
    void M(string str) {
        fixed (char* p = str) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment.ofArray(str.toCharArray())", result.GeneratedCode);
    }

    [Fact]
    public void PointerAddOffset_BytePointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte* q = p + 4;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.asSlice(4)", result.GeneratedCode);
    }

    [Fact]
    public void PointerIncrement_BytePointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            p++;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p = p.asSlice(1)", result.GeneratedCode);
    }

    [Fact]
    public void IntPointerAddOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            int* q = p + 4;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("asSlice((long)4 * 4)", result.GeneratedCode);
    }

    [Fact]
    public void PointerIndexAccess_IntPointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            int v = p[2];
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.get(ValueLayout.JAVA_INT, (long)2 * 4)", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeBlock_Stripped()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        unsafe { int x = 1; }
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
        Assert.Contains("int x = 1", result.GeneratedCode);
    }

    [Fact]
    public void UnsafeBlock_WithFixedInside()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        unsafe {
            fixed (byte* p = arr) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(arr)", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: Unsafe", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Byte()
    {
        var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_BYTE)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Char()
    {
        var result = Convert(@"
unsafe class Test {
    fixed char buffer[128];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_CHAR)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Int()
    {
        var result = Convert(@"
unsafe class Test {
    fixed int buffer[64];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_INT)", result.GeneratedCode);
    }

    [Fact]
    public void FixedStatement_Imports()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("import java.lang.foreign.MemorySegment", result.GeneratedCode);
        Assert.Contains("import java.lang.foreign.ValueLayout", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Imports()
    {
        var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
        Assert.True(result.Success);
        Assert.Contains("import java.lang.foreign.Arena", result.GeneratedCode);
    }

    [Fact]
    public void FixedNullPointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        fixed (byte* p = null) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment.NULL", result.GeneratedCode);
    }

    [Fact]
    public void FixedMultipleDeclarators()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] a, byte[] b) {
        fixed (byte* p1 = a, p2 = b) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p1 = MemorySegment.ofArray(a)", result.GeneratedCode);
        Assert.Contains("MemorySegment p2 = MemorySegment.ofArray(b)", result.GeneratedCode);
    }

    [Fact]
    public void NestedFixedStatements()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] a, int[] b) {
        fixed (byte* p = a) {
            fixed (int* q = b) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(a)", result.GeneratedCode);
        Assert.Contains("MemorySegment q = MemorySegment.ofArray(b)", result.GeneratedCode);
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
