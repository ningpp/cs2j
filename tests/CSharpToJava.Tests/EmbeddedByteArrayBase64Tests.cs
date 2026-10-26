using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// 大嵌入字节数组（≥1000 个常量）降位 <c>ArrayHelper.fromBase64("…")</c>：javac
/// 单方法 64KB 上限（代码过长/常量过多）。该助手自建数组，是自包含表达式——
/// 若外层仍拼 <c>new byte[]</c> 前缀，产物是 <c>new byte[]ArrayHelper.fromBase64(…)</c>
/// （真实链 xmlcoretest-tests FilePathUtil.s_fileN 的编译错误）。
/// </summary>
public class EmbeddedByteArrayBase64Tests
{
    [Fact]
    public void LargeConstantByteArrayField_EmitsSelfContainedBase64Call()
    {
        var literals = string.Join(", ", Enumerable.Range(0, 1200).Select(i => (i % 256).ToString()));
        var result = Convert($$"""
class Holder {
    private static byte[] s_bytes = new byte[] { {{literals}} };
    public static byte[] Bytes() => s_bytes;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("ArrayHelper.fromBase64(", code);
        // The helper allocates the array itself: no `new byte[]` prefix may remain.
        Assert.DoesNotContain("new byte[]ArrayHelper", code);
        Assert.DoesNotContain("new byte[] ArrayHelper", code);
        Assert.Contains("import io.github.ningpp.compat.ArrayHelper;", code);
    }

    [Fact]
    public void LargeConstantByteArrayWithNonConstantElement_KeepsLiteralInitializer()
    {
        var literals = string.Join(", ", Enumerable.Range(0, 1200).Select(i => (i % 256).ToString()));
        var result = Convert($$"""
class Holder {
    private static int Seed = 7;
    private static byte[] s_bytes = new byte[] { {{literals}}, Seed };
    public static byte[] Bytes() => s_bytes;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // A non-constant element means the array is materialized element-wise.
        Assert.DoesNotContain("ArrayHelper.fromBase64(", code);
        Assert.Contains("new byte[]", code);
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
