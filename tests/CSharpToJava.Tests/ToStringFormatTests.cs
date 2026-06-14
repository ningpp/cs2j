using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ToStringFormatTests
{
    [Fact]
    public void ByteToStringX2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.valueOf(b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringNoArgs_GeneratesStringValueOf()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.valueOf(b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MathHelper.formatNumeric", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringX2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringD8_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(""D8"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleToStringF2_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(double value)
    {
        return value.ToString(""F2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LongToStringX16_GeneratesMathHelperFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(long value)
    {
        return value.ToString(""X16"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SByteToStringX2_GeneratesMaskedFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(sbyte b)
    {
        return b.ToString(""X2"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShortToStringX4_GeneratesMaskedFormatNumeric()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(ushort value)
    {
        return value.ToString(""X4"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringWithProvider_StripsProvider()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(byte b)
    {
        return b.ToString(""X2"", System.Globalization.CultureInfo.InvariantCulture);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CultureInfo", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeToStringRoundtrip_UsesCompatToStringFormat()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public string Format(DateTime value)
    {
        return value.ToString(""o"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("value.toString(\"o\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("value.format(DateTimeFormatter.ofPattern(\"o\"))", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntToStringWithProviderOnly_GeneratesStringValueOf()
    {
        var result = Convert(@"
public class Sample
{
    public string Format(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.valueOf(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MathHelper.formatNumeric", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteToStringX2_UriEncodingScenario()
    {
        // Simulates the real-world URI encoding scenario where byte.ToString("X2")
        // is used to produce hex strings for percent-encoding
        var result = Convert(@"
public class Sample
{
    public string UriEncode(byte[] bytes)
    {
        string result = """";
        foreach (byte b in bytes)
        {
            result += ""%"" + b.ToString(""X2"");
        }
        return result;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.formatNumeric(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("& 0xFF", result.GeneratedCode, StringComparison.Ordinal);
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
