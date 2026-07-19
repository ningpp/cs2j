using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Convert.ToXxx(char) preserves C# char-to-numeric semantics
/// (Unicode code point) instead of being incorrectly mapped to Java parse methods.
/// </summary>
public class ConvertCharToNumericTests
{
    [Fact]
    public void ConvertToInt32_Char_UsesIntCast()
    {
        var result = Convert(@"
class Test {
    int FromChar(char c) => System.Convert.ToInt32(c);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("(int)(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.parseInt", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToInt32_String_StillUsesParseInt()
    {
        var result = Convert(@"
class Test {
    int FromString(string s) => System.Convert.ToInt32(s);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Integer.parseInt(s)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToInt64_Char_UsesLongCast()
    {
        var result = Convert(@"
class Test {
    long FromChar(char c) => System.Convert.ToInt64(c);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("(long)(c)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Long.parseLong", result.GeneratedCode, StringComparison.Ordinal);
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
