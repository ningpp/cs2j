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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
        Assert.Contains("MemorySegment p = __base", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*\(__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*&\s*0xFF\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, 0, ((byte)(42)))", result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(i\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*\(__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(i\)\)\)\s*&\s*0xFF\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Contains("p.set(ValueLayout.JAVA_BYTE, i, ((byte)(42)))", result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*1\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_BYTE,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\s*\+\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_CHAR,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(0\)\)\)\s*:\s*'\\0'\)",
            result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
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
        Assert.Contains("__base1.asSlice(p.address() - __base1.address() + 4)", result.GeneratedCode);
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
        Assert.Contains("__base1.asSlice(p.address() - __base1.address() + 1)", result.GeneratedCode);
        Assert.Contains("byteSize()", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofAddress", result.GeneratedCode);
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
        Assert.Contains("__base1.asSlice(p.address() - __base1.address() + (long)(4) * 4)", result.GeneratedCode);
    }

    [Fact]
    public void NestedPointerArithmetic_CharPointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(string str) {
        int startPos = 5;
        int len = 10;
        fixed (char* pChars = str) {
            char* p = pChars + startPos + len;
        }
    }
}");
        Assert.True(result.Success);
        // pChars + startPos generates a conditional expression with bounds check,
        // then + len chains .asSlice((long)(len) * 2) on the result.
        Assert.Matches(
            @"pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.asSlice\(pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\s*:\s*MemorySegment\.ofAddress\(pChars\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\)\.asSlice\(\(long\)\(len\)\s*\*\s*2\)",
            result.GeneratedCode);
    }

    [Fact]
    public void NestedPointerArithmetic_AsMethodArgument()
    {
        var result = Convert(@"
unsafe class Test {
    void Decode(char* p1, char* p2) { }
    void M(string str) {
        int startPos = 0;
        int len = 10;
        fixed (char* pChars = str) {
            Decode(pChars + startPos, pChars + startPos + len);
        }
    }
}");
        Assert.True(result.Success);
        // pChars + startPos generates a conditional expression with bounds check
        Assert.Matches(
            @"pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.asSlice\(pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\s*:\s*MemorySegment\.ofAddress\(pChars\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\)",
            result.GeneratedCode);
        // pChars + startPos + len chains .asSlice((long)(len) * 2) on the conditional result
        Assert.Matches(
            @"pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.asSlice\(pChars\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\s*:\s*MemorySegment\.ofAddress\(pChars\.address\(\)\s*\+\s*\(long\)\(startPos\)\s*\*\s*2\)\)\.asSlice\(\(long\)\(len\)\s*\*\s*2\)",
            result.GeneratedCode);
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
        Assert.Matches(
            @"\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(\(long\)\(2\)\s*\*\s*4\)\)\s*\+\s*4\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.get\(ValueLayout\.JAVA_INT,\s*\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(\(long\)\(2\)\s*\*\s*4\)\)\)\s*:\s*0\)",
            result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: Unsafe", result.GeneratedCode);
    }

    [Fact]
    public void StackAllocBytePointer_WithVariableLength_UsesMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    public static void DemoMethod(int numberOfLabels) {
        unsafe {
            byte* numbers = stackalloc byte[numberOfLabels];
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("MemorySegment numbers = MemorySegment.ofArray(new byte[numberOfLabels])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MemorySegment numbers = new byte[numberOfLabels]", result.GeneratedCode, StringComparison.Ordinal);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(a)", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(b)", result.GeneratedCode);
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
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(a)", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(b)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_AddressOfScalar()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        byte b = 42;
        fixed (byte* p = &b) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(new byte[] { b })", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_NestedFixed_AddressOfScalar()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        byte b = 42;
        fixed (byte* p = arr) {
            fixed (byte* q = &b) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(arr)", result.GeneratedCode);
        Assert.Contains("MemorySegment q = MemorySegment.ofArray(new byte[] { b })", result.GeneratedCode);
    }

    [Fact]
    public void PointerSubtractOffset_BytePointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            byte* q = p - 4;
        }
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
    }

    [Fact]
    public void PointerSubtractPointers_BytePointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            fixed (byte* q = arr) {
                long diff = p - q;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("(p.address() - q.address())", result.GeneratedCode);
    }

    [Fact]
    public void PointerComparison_LessThan()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] a, byte[] b) {
        fixed (byte* p = a) {
            fixed (byte* q = b) {
                bool v = p < q;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("p.address() < q.address()", result.GeneratedCode);
    }

    [Fact]
    public void PointerAddAssign_BytePointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(byte[] arr) {
        fixed (byte* p = arr) {
            p += 4;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("__base1.asSlice(p.address() - __base1.address() + 4)", result.GeneratedCode);
        Assert.Contains("byteSize()", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofAddress", result.GeneratedCode);
    }

    [Fact]
    public void PointerSubtractAssign_IntPointer()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr) {
        fixed (int* p = arr) {
            p -= 2;
        }
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
    }

    [Fact]
    public void StackAlloc_SpanUShort_WrapsInSpanConstructor()
    {
        var result = Convert(@"
namespace Demo {
    class Program {
        public static unsafe void DemoMethod(int NumberOfLabels) {
            unsafe {
                Span<ushort> numbers = stackalloc ushort[NumberOfLabels];
                numbers.Clear();
            }
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("new Span<>(new Short[NumberOfLabels])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("numbers.clear()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new short[NumberOfLabels]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StackAlloc_SpanInt_WrapsInSpanConstructor()
    {
        var result = Convert(@"
class Test {
    void M(int size) {
        Span<int> buf = stackalloc int[size];
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("new Span<>(new Integer[size])", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StackAlloc_ReadOnlySpanByte_WrapsInReadOnlySpanConstructor()
    {
        var result = Convert(@"
class Test {
    void M(int size) {
        ReadOnlySpan<byte> buf = stackalloc byte[size];
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("new ReadOnlySpan<>(new Byte[size])", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StackAlloc_PointerType_StillUsesMemorySegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int size) {
        byte* p = stackalloc byte[size];
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(new byte[size])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Span<>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharPointer_IndexWithCompoundExpression_ProducesCorrectOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr, int next) {
        fixed (char* p = arr) {
            char ch = p[next + 1];
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("(long)(next + 1) * 2", result.GeneratedCode);
        Assert.DoesNotContain("(long)next + 1 * 2", result.GeneratedCode);
    }

    [Fact]
    public void CharPointer_IndexWithCompoundExpression_Write_ProducesCorrectOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr, int next) {
        fixed (char* p = arr) {
            p[next + 1] = 'a';
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("(long)(next + 1) * 2", result.GeneratedCode);
        Assert.DoesNotContain("(long)next + 1 * 2", result.GeneratedCode);
    }

    [Fact]
    public void IntPointer_IndexWithCompoundExpression_ProducesCorrectOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(int[] arr, int i, int count) {
        fixed (int* p = arr) {
            int v = p[i + count];
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("(long)(i + count) * 4", result.GeneratedCode);
        Assert.DoesNotContain("(long)i + count * 4", result.GeneratedCode);
    }

    [Fact]
    public void CharPointer_ArithmeticWithCompoundExpression_ProducesCorrectOffset()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr, int next) {
        fixed (char* p = arr) {
            char* q = p + next + 1;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // p + next generates a conditional expression with bounds check,
        // then + 1 chains .asSlice((long)(1) * 2) on the result.
        Assert.Matches(
            @"p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(next\)\s*\*\s*2\s*<=\s*__base\d+\.byteSize\(\)\s*\?\s*__base\d+\.asSlice\(p\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*\(long\)\(next\)\s*\*\s*2\)\s*:\s*MemorySegment\.ofAddress\(p\.address\(\)\s*\+\s*\(long\)\(next\)\s*\*\s*2\)\)\.asSlice\(\(long\)\(1\)\s*\*\s*2\)",
            result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_AddressOfArrayElementZero()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        byte[] encodedBytes = new byte[4];
        fixed (byte* p = &encodedBytes[0]) { }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(encodedBytes)", result.GeneratedCode);
        Assert.DoesNotContain("new byte[] { encodedBytes[0] }", result.GeneratedCode);
    }

    [Fact]
    public void FixedCharPointer_AddressOfArrayElementZero()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        char[] chars = new char[10];
        fixed (char* p = &chars[0]) { }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(chars)", result.GeneratedCode);
        Assert.DoesNotContain("new char[] { chars[0] }", result.GeneratedCode);
    }

    [Fact]
    public void FixedBytePointer_AddressOfArrayElementNonZero()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        byte[] encodedBytes = new byte[4];
        fixed (byte* p = &encodedBytes[2]) { }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(encodedBytes)", result.GeneratedCode);
        Assert.Contains(".asSlice(2)", result.GeneratedCode);
    }

    [Fact]
    public void FixedIntPointer_AddressOfArrayElementNonZero()
    {
        var result = Convert(@"
unsafe class Test {
    void M() {
        int[] data = new int[10];
        fixed (int* p = &data[3]) { }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment.ofArray(data)", result.GeneratedCode);
        Assert.Contains("asSlice((long)(3) * 4)", result.GeneratedCode);
    }

    [Fact]
    public void PointerSubtractOffset_CharPointer_UsesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            char* q = p - 2;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains(".address()", result.GeneratedCode);
    }

    [Fact]
    public void PointerDerefSubtract_UsesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            p += 3;
            char ch = *(p - 1);
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
    }

    [Fact]
    public void PointerDerefSubtract_Write_UsesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr, char ch) {
        fixed (char* p = arr) {
            char* pnew = p - 2;
            *(pnew - 1) = ch;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains(".set(", result.GeneratedCode);
    }

    [Fact]
    public void PrefixDecrement_CharPointer_UsesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            char* end = p + 5;
            char ch = *--end;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
    }

    [Fact]
    public void PointerSubtractAssign_CharPointer_UsesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            p += 4;
            p -= 2;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
    }

    [Fact]
    public void PostfixIncrement_BacktrackDeref_UsesBacktrackVar()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            char ch = *p++;
            char prev = *(p - 1);
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // *(p - 1) after *p++ should use the backtrack variable, not asSlice(-N)
        Assert.DoesNotContain("asSlice(-", result.GeneratedCode);
    }

    [Fact]
    public void FixedPointer_BaseSegmentRegistered()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) { }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__base", result.GeneratedCode);
        Assert.Contains("MemorySegment __base", result.GeneratedCode);
    }

    [Fact]
    public void LocalPointerFromIdentifier_PropagatesBaseSegment()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* curPos = arr) {
            char* newPos = curPos;
            newPos++;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // newPos should inherit curPos's base segment, so newPos++ uses base segment
        // instead of newPos.asSlice(2)
        Assert.Matches(@"__base\d+\.asSlice\(newPos\.address\(\)\s*-\s*__base\d+\.address\(\)\s*\+\s*2\)", result.GeneratedCode);
        Assert.DoesNotContain("newPos.asSlice(2)", result.GeneratedCode);
    }

    [Fact]
    public void PointerIncrement_BeyondSegmentEnd_GeneratesOutOfBoundsProtection()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            p++;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // When pointer increment may go past the segment end, the generated code
        // should include out-of-bounds protection: a conditional expression that
        // uses asSlice when offset <= byteSize, otherwise falls back to
        // MemorySegment.ofAddress() to create a zero-length segment for address comparison.
        Assert.Contains("MemorySegment.ofAddress", result.GeneratedCode);
        Assert.Contains("byteSize()", result.GeneratedCode);
    }

    [Fact]
    public void PointerIndexAccess_WithPostIncrement_NoDoubleEvaluation()
    {
        var result = Convert(@"
unsafe class Test {
    void M(char[] arr) {
        fixed (char* p = arr) {
            int i = 0;
            char c = p[i++];
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // The offset expression should be evaluated only once, not twice.
        // If i++ appears in the generated code, it should appear exactly once
        // in the offset calculation, with the result stored in a temp variable.
        // Count occurrences of "i++" - should be exactly 1 (in the pre-statement)
        var matchCount = System.Text.RegularExpressions.Regex.Matches(
            result.GeneratedCode, @"i\+\+").Count;
        Assert.True(matchCount <= 1, $"i++ should appear at most once, but appeared {matchCount} times");
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
