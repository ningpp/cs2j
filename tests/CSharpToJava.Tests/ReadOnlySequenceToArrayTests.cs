using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReadOnlySequenceToArrayTests
{
    [Fact]
    public void ReadOnlySequenceOfByte_ToArray_UsesToByteArray()
    {
        var result = Convert(@"
using System.Buffers;

public class Sample
{
    public static byte[] M(ReadOnlySequence<byte> seq)
    {
        return seq.ToArray();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("BuffersExtensions.toByteArray(seq)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlySequenceOfInt_ToArray_StaysGeneric()
    {
        var result = Convert(@"
using System.Buffers;

public class Sample
{
    public static int[] M(ReadOnlySequence<int> seq)
    {
        return seq.ToArray();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("BuffersExtensions.toArray(seq)", code, StringComparison.Ordinal);
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
