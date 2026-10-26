using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# byte 的 Java 载体是 int（0..255，收窄用 <c>(e &amp; 0xFF)</c>）。极端常量若渲染成
/// <c>(byte)255</c>，Java 有符号 byte 会折成 -1：`value &gt; (byte)255` 对 0/1 也成立，
/// XmlUntypedStringConverter.int32ToByte 因此把 0/1 误判为溢出（真实链
/// read-content-as-tests 40 例 ArithmeticException）。
/// </summary>
public class PrimitiveExtremeConstantsTests
{
    [Fact]
    public void ByteMaxValue_RendersAsIntMagnitude()
    {
        var result = Convert("""
class C {
    int A() { return (int)System.Byte.MaxValue; }
    int B() { return System.Byte.MaxValue; }
    int D(int value) { return value > (int)System.Byte.MaxValue ? 1 : 0; }
    int E() { return (int)System.Byte.MinValue; }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("return 255;", code);
        Assert.Contains("value > 255", code);
        Assert.Contains("return 0;", code);
        // Signed-byte narrowing would wrap 255 to -1 and invert the bounds checks.
        Assert.DoesNotContain("(byte)255", code);
        Assert.DoesNotContain("Byte.MAX_VALUE", code);
    }

    [Fact]
    public void UShortAndULongExtremes_KeepCarrierMagnitude()
    {
        var result = Convert("""
class C {
    int A() { return (int)System.UInt16.MaxValue; }
    long B() { return unchecked((long)System.UInt64.MaxValue); }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("65535", code);
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
