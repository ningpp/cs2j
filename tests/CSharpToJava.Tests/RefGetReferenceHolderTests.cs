using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RefGetReferenceHolderTests
{
    [Fact]
    public void RefGetReferenceArg_WrapsInHolder()
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

        Assert.Contains("new io.github.ningpp.compat.IntHolder(MemoryMarshal.getReference(span))", code, StringComparison.Ordinal);
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
