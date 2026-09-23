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
