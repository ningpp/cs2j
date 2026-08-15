using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ByteArrayAsSpanToMutableSpanTests
{
    [Fact]
    public void ByteArrayAsSpan_AssignedToMutableSpan_WrapsInNewSpan()
    {
        var result = Convert(@"
public class Sample
{
    public static void M(byte[] rented, int length)
    {
        Span<byte> rentedSpan = rented.AsSpan(0, length);
        rentedSpan.Clear();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Span<Integer> rentedSpan = new Span<>(MemoryExtensions.asSpan(rented, 0, length))", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteArrayAsSpan_AssignedToReadOnlySpan_NoDoubleWrap()
    {
        var result = Convert(@"
public class Sample
{
    public static void M(byte[] rented, int length)
    {
        System.ReadOnlySpan<byte> r = rented.AsSpan(0, length);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("new Span<>(new ReadOnlySpan<>", code, StringComparison.Ordinal);
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
