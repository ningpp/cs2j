using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C# double literal suffix (d/D) preservation in Java output.
/// C# "1d" is a double literal; Java "1" is int.  Must emit "1.0" in Java.
/// </summary>
public class DoubleLiteralSuffixTests
{
    [Fact]
    public void DoubleSuffix_IntegerForm_EmitsDecimalPoint()
    {
        var source = @"
class Sample {
    void M() {
        double x = 1d / 3;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // "1d" should become "1.0" not "1"
        Assert.Contains("1.0 / 3", result.GeneratedCode);
    }

    [Fact]
    public void DoubleSuffix_WithDecimalPoint_StripsCleanly()
    {
        var source = @"
class Sample {
    void M() {
        double x = 1.5d + 2.0D;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // "1.5d" → "1.5", "2.0D" → "2.0"
        Assert.Contains("1.5", result.GeneratedCode);
        Assert.Contains("2.0", result.GeneratedCode);
        Assert.DoesNotContain("1.5d", result.GeneratedCode);
    }

    [Fact]
    public void HexBinaryLiterals_NotAffectedBySuffixProcessing()
    {
        var source = @"
class Sample {
    void M() {
        int a = 0xFFFD;
        int b = 0xFF;
        int c = 0xABCDEF;
        int d = 0b1010;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("0xFFFD", result.GeneratedCode);
        Assert.Contains("0xFF", result.GeneratedCode);
        Assert.Contains("0xABCDEF", result.GeneratedCode);
        Assert.Contains("0b1010", result.GeneratedCode);
        Assert.DoesNotContain("0xFFF.0", result.GeneratedCode);
        Assert.DoesNotContain("0xFFf", result.GeneratedCode);
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
