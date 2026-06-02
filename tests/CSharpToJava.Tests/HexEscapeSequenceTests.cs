using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class HexEscapeSequenceTests
{
    [Fact]
    public void CharHexEscape_Space_ConvertedToJavaCompatible()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x20';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x20", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("' '", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_Tab_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x9';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x9", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\t'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_Newline_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\xA';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\xA", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_CarriageReturn_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\xD';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\xD", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\r'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_ControlChar_ConvertedToUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x7';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x7", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\u0007'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_VerticalTab_ConvertedToUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\xB';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\xB", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\u000B'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_EscapeChar_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x5C';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x5C", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\\\'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_SingleQuote_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x27';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x27", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\''", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_PrintableChar_PreservedAsIs()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x41';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x41", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'A'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_FourDigitHex_ConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x000A';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x000A", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_Backspace_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x8';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x8", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\b'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_FormFeed_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\xC';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\xC", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\f'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharHexEscape_NullChar_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\x0';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\0'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsWhitespace_FullMethod_AllHexEscapesConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                private static bool IsWhitespace(char ch)
                {
                    return ch == '\x20' || ch == '\x9' || ch == '\xA' || ch == '\xD';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");

        Assert.DoesNotContain("\\x20", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\x9", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xA", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xD", result.GeneratedCode, StringComparison.Ordinal);

        Assert.Contains("' '", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\t'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\r'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringHexEscape_AllXmlWhitespaceChars_ConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public string M()
                {
                    return "\x20\x9\xA\xD";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");

        Assert.DoesNotContain("\\x20", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\x9", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xA", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xD", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringHexEscape_TabAndNewline_ConvertedToJavaEscapes()
    {
        var result = Convert("""
            public class C
            {
                public string M()
                {
                    return "\x9\xA";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\t", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\n", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringHexEscape_ControlChar_ConvertedToUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public string M()
                {
                    return "\x7";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x7", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\u0007", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringHexEscape_PrintableChar_PreservedInOutput()
    {
        var result = Convert("""
            public class C
            {
                public string M()
                {
                    return "\x41";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x41", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\"A\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchOnChar_HexEscape_ConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public string M(char c)
                {
                    switch (c)
                    {
                        case '\x7':
                            return "alert";
                        case '\xB':
                            return "vtab";
                        default:
                            return "other";
                    }
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\u0007'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\u000B'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\x7", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xB", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedHexAndStandardEscapes_AllConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public bool M(char ch)
                {
                    return ch == '\t' || ch == '\x9' || ch == '\n' || ch == '\xA';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\x9", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\xA", result.GeneratedCode, StringComparison.Ordinal);
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
