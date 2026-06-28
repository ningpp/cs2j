using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SubstringConversionTests
{
    [Fact]
    public void Substring_OneArg_ConvertsDirectly()
    {
        var result = Convert(@"
class Sample
{
    public string GetSuffix(string s)
    {
        return s.Substring(2);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("s.substring(2)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Substring_TwoArgs_UsesStringHelperForDotNetRangeSemantics()
    {
        var result = Convert(@"
class Sample
{
    public string GetMiddle(string s)
    {
        return s.Substring(2, 3);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(s, 2, 3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Substring_TwoArgs_VariableArgs_UsesStringHelperForDotNetRangeSemantics()
    {
        var result = Convert(@"
class Sample
{
    public string Extract(string s, int start, int len)
    {
        return s.Substring(start, len);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(s, start, len)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Substring_TwoArgs_ExpressionArgs_UsesStringHelperForDotNetRangeSemantics()
    {
        var result = Convert(@"
class Sample
{
    public string Extract(string s, int offset)
    {
        return s.Substring(offset + 1, 5);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(s, offset + 1, 5)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Substring_TwoArgs_LengthProperty_UsesStringHelperForDotNetRangeSemantics()
    {
        var result = Convert(@"
class Sample
{
    public string GetPrefix(string s)
    {
        return s.Substring(0, s.Length - 1);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(s, 0, s.length() - 1)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilder_ToString_TwoArgs_UsesStringHelperForDotNetRangeSemantics()
    {
        var result = Convert(@"
using System.Text;
class Sample
{
    public string GetPart(StringBuilder sb)
    {
        return sb.ToString(2, 3);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(sb.toString(), 2, 3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Substring_ChainedWithToLower_ConvertsCorrectly()
    {
        var result = Convert(@"
class Sample
{
    public string LowerFirst(string attrString)
    {
        attrString = attrString.Substring(0, 1).ToLower() + attrString.ToLower().Substring(1, attrString.Length - 1);
        return attrString;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("StringHelper.substring(attrString, 0, 1)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.substring(attrString.toLowerCase(), 1, attrString.length() - 1)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToLower_WithCultureInfo_ConvertsCultureArgumentToLocale()
    {
        var result = Convert(@"
using System.Globalization;

class Sample
{
    public string LowerFirst(string attrString)
    {
        attrString = attrString.Substring(0, 1).ToLower(CultureInfo.InvariantCulture)
            + attrString.Substring(1, attrString.Length - 1);
        return attrString;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains(
            "StringHelper.substring(attrString, 0, 1).toLowerCase(CultureInfo.getInvariantCulture().toLocale())",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "toLowerCase(CultureInfo.getInvariantCulture())",
            result.GeneratedCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToLower_WithFullyQualifiedCultureInfo_DoesNotAppendToLocaleToLocaleRoot()
    {
        var result = Convert(@"
class Sample
{
    public string Lower(string value)
    {
        return value.ToLower(System.Globalization.CultureInfo.InvariantCulture);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains(
            "value.toLowerCase(CultureInfo.getInvariantCulture().toLocale())",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Locale.ROOT.toLocale()", result.GeneratedCode, StringComparison.Ordinal);
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
