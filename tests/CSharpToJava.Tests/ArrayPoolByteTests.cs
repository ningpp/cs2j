using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayPoolByteTests
{
    [Fact]
    public void ArrayPoolByte_RentAndReturn_UseByteArrayHelpers()
    {
        var result = Convert(@"
using System.Buffers;

public class Sample
{
    public static void M(int length)
    {
        byte[] arr = ArrayPool<byte>.Shared.Rent(length);
        ArrayPool<byte>.Shared.Return(arr);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("import io.github.ningpp.compat.ArrayPool", code, StringComparison.Ordinal);
        Assert.Contains("ArrayPool.rentSharedByteArray(length)", code, StringComparison.Ordinal);
        Assert.Contains("ArrayPool.returnSharedByteArray(arr)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("rentShared(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("getShared()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayPoolByte_ReturnWithClear_UsesClearOverload()
    {
        var result = Convert(@"
using System.Buffers;

public class Sample
{
    public static void M(byte[] arr)
    {
        ArrayPool<byte>.Shared.Return(arr, true);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ArrayPool.returnSharedByteArray(arr, true)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("getShared()", code, StringComparison.Ordinal);
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
