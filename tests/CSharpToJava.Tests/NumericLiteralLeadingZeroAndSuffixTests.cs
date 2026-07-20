using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# numeric literals with leading zeros or values exceeding int range
/// are converted to valid Java literals.
///
/// 1. C# allows leading zeros in decimal literals (e.g. 08 = 8), but Java interprets
///    them as octal (08 = illegal octal digit). The converter must strip leading zeros.
/// 2. C# integer literals that exceed int.MaxValue are implicitly long, but Java requires
///    an explicit L suffix for values outside int range.
/// </summary>
public class NumericLiteralLeadingZeroAndSuffixTests
{
    [Fact]
    public void LeadingZeroInDecimalLiteral_StripsLeadingZero()
    {
        var result = Convert(@"
class Test
{
    public int M(int x) { return x; }
    void N()
    {
        M(08);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should NOT produce octal literal 08 (invalid in Java)
        Assert.DoesNotContain("(08)", result.GeneratedCode);
        // Should produce decimal 8
        Assert.Contains("(8)", result.GeneratedCode);
    }

    [Fact]
    public void LeadingZeroMonthValue_StripsLeadingZero()
    {
        var result = Convert(@"
class Test
{
    void M()
    {
        int month = 06;
        int day = 09;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("= 06;", result.GeneratedCode);
        Assert.DoesNotContain("= 09;", result.GeneratedCode);
        Assert.Contains("= 6;", result.GeneratedCode);
        Assert.Contains("= 9;", result.GeneratedCode);
    }

    [Fact]
    public void UnsuffixedLongValue_AddsLSuffix()
    {
        var result = Convert(@"
class Test
{
    void M()
    {
        long x = 12760929000000000;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should add L suffix for values exceeding int range
        Assert.Contains("12760929000000000L", result.GeneratedCode);
    }

    [Fact]
    public void UnsuffixedIntMaxValuePlusOne_AddsLSuffix()
    {
        var result = Convert(@"
class Test
{
    void M()
    {
        long x = 2147483648;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // 2147483648 = int.MaxValue + 1, needs L suffix in Java
        Assert.Contains("2147483648L", result.GeneratedCode);
    }

    [Fact]
    public void NormalIntValue_NoLSuffix()
    {
        var result = Convert(@"
class Test
{
    void M()
    {
        int x = 42;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Normal int value should NOT have L suffix
        Assert.Contains("= 42;", result.GeneratedCode);
        Assert.DoesNotContain("42L", result.GeneratedCode);
    }

    [Fact]
    public void ZeroValue_NoChange()
    {
        var result = Convert(@"
class Test
{
    void M()
    {
        int x = 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("= 0;", result.GeneratedCode);
    }

    [Fact]
    public void ExponentLiteral_ZeroExponent_PreservesMantissa()
    {
        var result = Convert(@"
class Test
{
    bool M(double v)
    {
        return v == -0e0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The literal must stay as -0e0; stripping the mantissa zero gives -e0,
        // which is not a valid Java literal.
        Assert.Contains("-0e0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("-e0", result.GeneratedCode, StringComparison.Ordinal);
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
