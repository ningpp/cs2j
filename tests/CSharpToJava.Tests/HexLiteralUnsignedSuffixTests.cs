using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C# hex/binary literals with unsigned suffix (U).
/// Java does not support 'U' suffix on hex literals; the converter must strip it.
/// </summary>
public class HexLiteralUnsignedSuffixTests
{
    [Fact]
    public void HexLiteralWithUSuffix_StripsSuffix()
    {
        var result = Convert(@"
public class Sample
{
    public int M()
    {
        int x = 0xFF;
        int y = 0xFFFF;
        return x + y;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("0xFF", result.GeneratedCode);
        Assert.Contains("0xFFFF", result.GeneratedCode);
    }

    [Fact]
    public void HexLiteralWithLongSuffix_PreservesSuffix()
    {
        var result = Convert(@"
public class Sample
{
    public long M()
    {
        return 0x00ffffffffffffffL;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // javac rejects hex literals without L suffix once they exceed 8 hex digits
        // ("integer number too large"), so the L suffix must be preserved.
        Assert.Contains("0x00ffffffffffffffL", result.GeneratedCode);
    }

    [Fact]
    public void HexLiteralWithUnsignedLongSuffix_KeepsLongSuffix()
    {
        var result = Convert(@"
public class Sample
{
    public long M()
    {
        return 0xFFFFFFFFFFFFFFF0UL;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("0xFFFFFFFFFFFFFFF0L", result.GeneratedCode);
        Assert.DoesNotContain("UL", result.GeneratedCode);
    }

    [Fact]
    public void HexLiteralInExpression_StripsUSuffix()
    {
        var result = Convert(@"
public class Sample
{
    public int M(byte value)
    {
        int difference = (((int)value & 0xF0) << 4) + ((int)value & 0x0F) - 0x8989;
        return difference;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should NOT have U suffix on hex literals in Java
        Assert.DoesNotContain("0xF0U", result.GeneratedCode);
        Assert.DoesNotContain("0x0FU", result.GeneratedCode);
        Assert.DoesNotContain("0x8989U", result.GeneratedCode);
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
