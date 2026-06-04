using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PrimitiveStaticConstantMappingTests
{
    [Fact]
    public void BoxedDoubleNaN_InStaticPropertyGetter_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public static double NoFixedPosition
    {
        get { return Double.NaN; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveDoubleNaN_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public double GetNaN()
    {
        return double.NaN;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt32MaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxIPv4Value = UInt32.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("4294967295L", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt32MinValue_EmitsZero()
    {
        var result = Convert("""
public class Sample
{
    private const uint Min = UInt32.MinValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("= 0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MIN_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UintKeywordMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxIPv4Value = uint.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("4294967295L", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const int MaxPort = UInt16.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("65535", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Short.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortKeywordMaxValue_EmitsLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const int MaxPort = ushort.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("65535", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Short.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ULongMaxValue_EmitsHexLiteral()
    {
        var result = Convert("""
public class Sample
{
    private const long MaxVal = UInt64.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("0xFFFFFFFFFFFFFFFFL", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Long.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Int32MaxValue_StillUsesIntegerMAX_VALUE()
    {
        var result = Convert("""
public class Sample
{
    private const int Max = Int32.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntKeywordMaxValue_StillUsesIntegerMAX_VALUE()
    {
        var result = Convert("""
public class Sample
{
    private const int Max = int.MaxValue;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
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
