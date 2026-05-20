using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CharLiteralEscapeSequenceTests
{
    [Fact]
    public void SwitchOnChar_AlertEscape_ConvertedToUnicodeEscape()
    {
        var result = Convert(@"
public class C
{
    public string Test(char c)
    {
        switch (c)
        {
            case '\a':
                return ""alert"";
            default:
                return ""other"";
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("'\\u0007'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("'\\a'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchOnChar_VerticalTabEscape_ConvertedToUnicodeEscape()
    {
        var result = Convert(@"
public class C
{
    public string Test(char c)
    {
        switch (c)
        {
            case '\v':
                return ""vtab"";
            default:
                return ""other"";
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("'\\u000B'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("'\\v'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchOnChar_JavaSupportedEscapes_PreservedAsIs()
    {
        var result = Convert(@"
public class C
{
    public string Test(char c)
    {
        switch (c)
        {
            case '\t': return ""tab"";
            case '\n': return ""newline"";
            case '\r': return ""cr"";
            case '\0': return ""null"";
            case '\b': return ""backspace"";
            case '\f': return ""formfeed"";
            default: return ""other"";
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("'\\t'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\r'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\0'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\b'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\f'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringFormat_WithCastIFormatProvider_ProviderStripped()
    {
        var result = Convert(@"
using System;
using System.Globalization;

public class C
{
    public string Test(char input)
    {
        return string.Format((IFormatProvider) CultureInfo.InvariantCulture, ""{0}"", (object) input);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.format(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CultureInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("IFormatProvider", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharToString_FullMethod_AllEscapesConvertedCorrectly()
    {
        var result = Convert(@"
using System;
using System.Globalization;

public class CharHelper
{
    protected static string CharToString(char input)
    {
        switch (input)
        {
            case char.MinValue:
                return ""'\0'"";
            case '\a':
                return ""'\a'"";
            case '\b':
                return ""'\b'"";
            case '\t':
                return ""'\t'"";
            case '\n':
                return ""'\n'"";
            case '\v':
                return ""'\v'"";
            case '\f':
                return ""'\f'"";
            case '\r':
                return ""'\r'"";
            default:
                return string.Format((IFormatProvider) CultureInfo.InvariantCulture, ""'{0}'"", (object) input);
        }
    }
}");

        Assert.True(result.Success);

        // C#-only escapes must be converted to Java Unicode escapes
        Assert.Contains("case '\\u0007'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\u000B'", result.GeneratedCode, StringComparison.Ordinal);

        // Java-supported escapes stay as-is
        Assert.Contains("case Character.MIN_VALUE", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\b'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\t'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\f'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case '\\r'", result.GeneratedCode, StringComparison.Ordinal);

        // IFormatProvider / CultureInfo stripped from String.format
        Assert.Contains("String.format(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CultureInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("IFormatProvider", result.GeneratedCode, StringComparison.Ordinal);
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
