using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# const fields are rendered as valid Java literals.
/// </summary>
public class ConstFieldLiteralRenderingTests
{
    [Fact]
    public void ConstChar_NewLineAndReturn_Escaped()
    {
        var result = Convert(@"
class Test
{
    private const char NewLine = '\n';
    private const char Return = '\r';
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static final char NewLine = '\\n';", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private static final char Return = '\\r';", result.GeneratedCode, StringComparison.Ordinal);
        // The generated code must not contain an actual newline or carriage return inside a char literal.
        Assert.DoesNotContain("'\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("'\r'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstChar_TabAndNullAndQuote_Escaped()
    {
        var result = Convert(@"
class Test
{
    private const char Tab = '\t';
    private const char NullChar = '\0';
    private const char Quote = '\'';
    private const char Backslash = '\\';
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static final char Tab = '\\t';", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private static final char NullChar = '\\0';", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private static final char Quote = '\\'';", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private static final char Backslash = '\\\\';", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstChar_ControlCharacter_EscapedAsUnicode()
    {
        var result = Convert(@"
class Test
{
    private const char Bell = '\u0007';
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private static final char Bell = '\\u0007';", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstUInt_HighBitHexValue_RendersHex()
    {
        var result = Convert(@"
class Test
{
    private const uint TypeMask = 0xFF000000;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Must not emit the unsigned decimal 4278190080, which is larger than Java int.MaxValue.
        Assert.DoesNotContain("4278190080", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("0xFF000000", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstUInt_SignBitHexValue_RendersHex()
    {
        var result = Convert(@"
class Test
{
    private const uint NegativeBit = 0x80000000;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Must not emit the unsigned decimal 2147483648, which is larger than Java int.MaxValue.
        Assert.DoesNotContain("2147483648", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("0x80000000", result.GeneratedCode, StringComparison.Ordinal);
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
