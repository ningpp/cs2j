using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class BufferBlockCopyArrayArgumentTests
{
    [Fact]
    public void Buffer_BlockCopy_CharArrayArg_NotWrappedInCSharpArray()
    {
        var result = Convert("""
using System;

class Program
{
    static void Compress(char[] dest, int start, ref int destLength)
    {
        Buffer.BlockCopy(dest, start << 1, dest, (start + 1) << 1, (destLength - start) << 1);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // Buffer.blockCopy should receive the raw char[] array, not CSharpArray.of(dest)
        Assert.DoesNotContain("CSharpArray.of(dest)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Buffer.blockCopy(dest,", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_BlockCopy_ByteArrayArg_NotWrappedInCSharpArray()
    {
        var result = Convert("""
using System;

class Program
{
    static void Copy(byte[] src, byte[] dst)
    {
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.DoesNotContain("CSharpArray.of(src)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpArray.of(dst)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Buffer.blockCopy(src,", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_GetByte_CharArrayArg_NotWrappedInCSharpArray()
    {
        var result = Convert("""
using System;

class Program
{
    static byte ReadChar(char[] data, int offset)
    {
        return Buffer.GetByte(data, offset);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.DoesNotContain("CSharpArray.of(data)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Buffer.getByte(data,", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_ByteLength_CharArrayArg_NotWrappedInCSharpArray()
    {
        var result = Convert("""
using System;

class Program
{
    static int GetSize(char[] data)
    {
        return Buffer.ByteLength(data);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.DoesNotContain("CSharpArray.of(data)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Buffer.byteLength(data)", result.GeneratedCode, StringComparison.Ordinal);
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
