using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the byte/span/pointer/ValueTask conversion cluster found
/// while building the converted System.Text.Json project:
/// 1. ReadOnlyMemory&lt;byte&gt;.ToArray() must materialize byte[] (not Integer[]).
/// 2. Non-async ValueTask-returning methods expose the underlying CompletableFuture.
/// 3. Casting a pointer (MemorySegment) to int must use .address().
/// 4. fixed (byte* p = &amp;MemoryMarshal.GetReference(span)) must pin the whole span.
/// 5. Target-typed span conditionals must not be double-wrapped (new Span<>(Span<>)).
/// </summary>
public class JsonByteSpanPointerValueTaskTests
{
    [Fact]
    public void ReadOnlySpanByteToArray_YieldsByteArray()
    {
        var result = Convert(@"
class Sample
{
    public byte[] M(System.ReadOnlySpan<byte> s)
    {
        return s.ToArray();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Integer[] from the boxed span must be narrowed back to byte[].
        Assert.Contains("s.toByteArray()", code);
        Assert.DoesNotContain("s.toArray()", code);
    }

    [Fact]
    public void NonAsyncValueTaskMethod_ExposesCompletableFuture()
    {
        var result = Convert(@"
using System.Threading.Tasks;

class Sample
{
    public ValueTask M(System.Buffers.ArrayBufferWriter<byte> w, Stream s)
    {
        return s.WriteAsync(w.WrittenMemory, default);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Callers await the method with .join(), so the return type must be the
        // underlying CompletableFuture, not the opaque ValueTask marker.
        Assert.Contains("public CompletableFuture m(", code);
        Assert.DoesNotContain("public ValueTask m(", code);
        Assert.Contains("return s.writeAsync(", code);
    }

    [Fact]
    public void PointerCastToInt_UsesAddress()
    {
        var result = Convert(@"
unsafe class Sample
{
    public int M(char* p)
    {
        return (int)p & 0x2;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // MemorySegment cannot be cast to int directly; use the segment address.
        Assert.Contains("(int)(p.address())", code);
    }

    [Fact]
    public void FixedOverGetReferenceSpan_PinsWholeSpan()
    {
        var result = Convert(@"
class Sample
{
    public unsafe void M(System.ReadOnlySpan<byte> s)
    {
        fixed (byte* p = &System.Runtime.InteropServices.MemoryMarshal.GetReference(s))
        {
            p[0] = 1;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The ref element surfaces boxed (Integer); pinning must materialize the
        // whole backing byte[] so pointer arithmetic over the span stays in bounds,
        // and no (byte)(Integer) narrowing cast may be emitted.
        Assert.Contains("MemorySegment p = MemorySegment.ofArray(s.toByteArray());", code);
        Assert.DoesNotContain("(byte)(s", code);
    }

    [Fact]
    public void TargetTypedSpanConditional_NotDoubleWrapped()
    {
        var result = Convert(@"
class Sample
{
    public void M(bool b)
    {
        byte[] a1 = new byte[10];
        byte[] a2 = new byte[20];
        Span<byte> s = b ? a1 : a2;
        System.ReadOnlySpan<byte> r = b ? a1 : a2;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Branches are adapted once by the conditional transformer; the enclosing
        // assignment/declaration must not wrap the ternary again (new Span<>(Span<>)
        // has no matching compat constructor).
        Assert.Contains("new Span<Integer>(a1) : new Span<Integer>(a2)", code);
        Assert.Contains("new ReadOnlySpan<Integer>(a1) : new ReadOnlySpan<Integer>(a2)", code);
        Assert.DoesNotContain("new Span<Integer>((b", code);
        Assert.DoesNotContain("new ReadOnlySpan<Integer>((b", code);
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
