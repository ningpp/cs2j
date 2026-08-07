using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests the unsafe fixed-pointer-over-span round trip used by HexConverter.ToString:
/// fixed (byte* p = span) + string.Create(state: (Ptr: (IntPtr)p, len), lambda that
/// reconstructs ReadOnlySpan&lt;byte&gt; from the pointer. The converter must keep the
/// pattern compilable: fixed init over a span needs a real array, named tuple fields
/// (Ptr/Length) must lower to vavr _N() accessors, the (IntPtr) cast over a
/// MemorySegment is a no-op, and new ReadOnlySpan&lt;byte&gt;((byte*)ptr, len) must use
/// the compat segment-reconstruction helper.
/// </summary>
public class FixedPointerSpanRoundTripTests
{
    [Fact]
    public void FixedOverSpan_TuplePointerState_ReconstructsSpanCompilably()
    {
        var result = Convert(@"
class HexLike
{
    public static unsafe string ToString(System.ReadOnlySpan<byte> bytes)
    {
        fixed (byte* bytesPtr = bytes)
        {
            return string.Create(bytes.Length * 2, (Ptr: (System.IntPtr)bytesPtr, bytes.Length), static (chars, args) =>
            {
                var ros = new System.ReadOnlySpan<byte>((byte*)args.Ptr, args.Length);
                for (int i = 0; i < ros.Length; i++)
                {
                    chars[i] = 'A';
                }
            });
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // fixed over a span must pin a real array, not the span object itself.
        Assert.DoesNotContain("MemorySegment.ofArray(bytes)", code);
        Assert.Contains("bytes.toByteArray()", code);

        // (IntPtr) cast over the MemorySegment must not leak an IntPtr type name.
        Assert.DoesNotContain("IntPtr", code);

        // Named tuple fields lower to positional vavr accessors.
        Assert.Contains("args._1()", code);
        Assert.Contains("args._2()", code);
        Assert.DoesNotContain("args.Ptr", code);
        Assert.DoesNotContain("args.Length", code);

        // Pointer+length reconstruction goes through the compat helper.
        Assert.Contains("fromByteSegment(args._1(), args._2())", code);
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
