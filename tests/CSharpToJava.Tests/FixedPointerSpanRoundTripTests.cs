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
