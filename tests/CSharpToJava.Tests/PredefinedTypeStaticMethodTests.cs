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
    public void ConversionPipeline_StringIsNullOrEmpty_GeneratesNullOrIsEmptyCheck()
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
        Assert.Contains("(s == null || s.isEmpty())", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.isNullOrEmpty", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("StringHelper.isNullOrEmpty", result.GeneratedCode, StringComparison.Ordinal);
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
