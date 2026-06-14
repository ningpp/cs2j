using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for correct handling of static method calls on C# predefined types
/// (string, double, int, etc.) when using keyword syntax (e.g. string.IsNullOrEmpty).
/// Regression tests for GitHub issue #1.
/// </summary>
public class PredefinedTypeStaticMethodTests
{
    [Fact]
    public void ConversionPipeline_StringIsNullOrEmpty_GeneratesStringHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Check(string s)
    {
        return string.IsNullOrEmpty(s);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("StringHelper.isNullOrEmpty(s)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(s == null || s.isEmpty())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_StringIsNullOrWhiteSpace_GeneratesStringHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Check(string s)
    {
        return string.IsNullOrWhiteSpace(s);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("StringHelper.isNullOrWhiteSpace(s)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.isNullOrWhiteSpace", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_DoubleTryParse_GeneratesMathHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Parse(string s)
    {
        double result;
        return double.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryParseDouble(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Double.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_FloatTryParse_GeneratesMathHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Parse(string s)
    {
        float result;
        return float.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryParseFloat(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Float.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_IntTryParse_GeneratesMathHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Parse(string s)
    {
        int result;
        return int.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryParseInt(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_LongTryParse_GeneratesMathHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Parse(string s)
    {
        long result;
        return long.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryParseLong(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Long.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_BoolTryParse_GeneratesMathHelperCall()
    {
        var result = Convert(@"
class Sample
{
    public bool Parse(string s)
    {
        bool result;
        return bool.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryParseBool(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Boolean.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_ByteTryParseWithNumberStyles_UsesIntHolderCompatibleHelper()
    {
        var result = Convert(@"
using System;
using System.Globalization;

class Sample
{
    public bool Parse(string s)
    {
        byte result;
        return Byte.TryParse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("IntHolder", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.tryParseByte(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ByteHolder", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Byte.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_UnsignedPrimitiveParseAndTryParse_GenerateValidJavaHelpers()
    {
        var result = Convert(@"
using System;
using System.Globalization;

class Sample
{
    public void Parse(string s)
    {
        ushort us = UInt16.Parse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo);
        uint ui = UInt32.Parse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo);
        ulong ul = UInt64.Parse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo);
    }

    public void Try(string s, out UInt16 us, out UInt32 ui, out UInt64 ul)
    {
        UInt16.TryParse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out us);
        UInt32.TryParse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out ui);
        UInt64.TryParse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out ul);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.parseUShort(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.parseUInt(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.parseULong(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.tryParseUShort(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.tryParseUInt(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("MathHelper.tryParseULong(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ushort.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("uint.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ulong.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UInt16.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UInt32.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UInt64.", result.GeneratedCode, StringComparison.Ordinal);
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
