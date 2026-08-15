using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RefGetReferenceHolderTests
{
    [Fact]
    public void RefGetReferenceByteArg_UsesSpanAwareHolder()
    {
        var result = Convert(@"
using System.Runtime.InteropServices;

public class Sample
{
    private static int IndexOfOrLessThan(ref byte searchSpace, byte value0, byte value1, byte lessThan, int length)
    {
        return searchSpace;
    }

    public static int M(System.ReadOnlySpan<byte> span)
    {
        return IndexOfOrLessThan(ref MemoryMarshal.GetReference(span), (byte)0, (byte)1, (byte)2, span.Length);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // ref byte scans (e.g. Unsafe.AddByteOffset) must retain the span so
        // offset reads work; a plain IntHolder only carries the first byte.
        Assert.Contains("Unsafe.createByteSpanRef(span)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new io.github.ningpp.compat.IntHolder(MemoryMarshal.getReference(span))", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RefGetReferenceNonByteArg_StillWrapsInPlainHolder()
    {
        var result = Convert(@"
using System.Runtime.InteropServices;

public class Sample
{
    private static int Idx(ref int first)
    {
        return first;
    }

    public static int M(System.ReadOnlySpan<int> span)
    {
        return Idx(ref MemoryMarshal.GetReference(span));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new io.github.ningpp.compat.IntHolder(MemoryMarshal.getReference(span))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsafe.createByteSpanRef(span)", code, StringComparison.Ordinal);
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
