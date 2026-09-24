using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Large byte[] initializers (>= 512 elements) are Base64-encoded at conversion
/// time, which requires evaluating every element to a concrete byte. Real-world
/// initializers contain cast expressions such as (byte)0xBf or (byte)200 (from
/// generated Unicode range tables); the converter must evaluate them instead of
/// parsing the cast text as a number.
/// </summary>
public class ByteArrayBase64CastElementTests
{
    [Fact]
    public void LargeByteArrayInitializer_WithCastElements_IsEncoded()
    {
        // 520 elements to cross the 512-element Base64 threshold, with hex and
        // decimal cast expressions mixed in, mirroring generated range tables.
        var bytes = new System.Text.StringBuilder();
        bytes.Append("public static byte[] Data = { ");
        for (int i = 0; i < 520; i++)
        {
            if (i > 0) bytes.Append(", ");
            bytes.Append(i switch
            {
                0 => "(byte)0xBf",
                1 => "(byte)200",
                2 => "0x7F",
                _ => (i % 256).ToString(),
            });
        }
        bytes.Append(" };");

        var source = "namespace TestNs\n" +
                     "{\n" +
                     "    public class TestClass\n" +
                     "    {\n" +
                     "        " + bytes + "\n" +
                     "    }\n" +
                     "}\n";

        var result = Convert(source);

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("java.util.Base64.getDecoder().decode", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeByteArrayInitializer_CastOnlyHexValues_EncodedCorrectly()
    {
        // Every element written as a cast so any element would fail if the cast
        // is not unwrapped/folded. Values cycle 0..255 as hex casts.
        var bytes = new System.Text.StringBuilder();
        bytes.Append("public static byte[] Data = { ");
        for (int i = 0; i < 520; i++)
        {
            if (i > 0) bytes.Append(", ");
            bytes.Append($"(byte)0x{(i % 256):X2}");
        }
        bytes.Append(" };");

        var source = "namespace TestNs\n" +
                     "{\n" +
                     "    public class TestClass\n" +
                     "    {\n" +
                     "        " + bytes + "\n" +
                     "    }\n" +
                     "}\n";

        var result = Convert(source);

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("java.util.Base64.getDecoder().decode", result.GeneratedCode ?? "", StringComparison.Ordinal);
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
