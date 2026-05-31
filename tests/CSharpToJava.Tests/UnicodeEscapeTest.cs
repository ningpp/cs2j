using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnicodeEscapeTest
{
    [Fact]
    public void UnicodeLineFeed_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string d = "\u000A";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\n", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000A", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeCarriageReturn_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string c = "\u000D";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\r", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeCRLF_ConvertedToJavaEscapes()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string c = "\u000D\u000A";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\r\\n", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000A", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeSpace_PreservedInOutput()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string a = "\u0020\u0020";
                    string b = "\u0020";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\"  \"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\" \"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeDoubleQuote_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\u0022";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0022", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeBackslash_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\u005C";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\\\", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u005C", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeAlert_ConvertedToJavaUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\u0007";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\u0007", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeVerticalTab_ConvertedToJavaUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\u000B";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\u000B", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedUnicodeEscapes_AllConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string a = "\u0020\u0020";
                    string b = "\u0020";
                    string c = "\u000D\u000A";
                    string d = "\u000A";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.DoesNotContain("\\u000A", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\n", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeLineFeed_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u000A';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000A", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeCarriageReturn_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u000D';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\r'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeSingleQuote_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u0027';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\''", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0027", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeBackslash_ConvertedToJavaEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u005C';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\\\'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u005C", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeAlert_ConvertedToJavaUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u0007';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\u0007'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharUnicodeVerticalTab_ConvertedToJavaUnicodeEscape()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    char ch = '\u000B';
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("'\\u000B'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeUnicodeEscapes_PreservedInOutput()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\u0041";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\"A\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringWithStandardEscapes_StillWorks()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string a = "hello\tworld";
                    string b = "line1\nline2";
                    string c = "back\\slash";
                    string d = "quote\"inside";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\t", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\n", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\\\", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\\\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCharEscape_ConvertedCorrectly()
    {
        var result = Convert("""
            public class C
            {
                public void M()
                {
                    string s = "\0";
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("\\0", result.GeneratedCode, StringComparison.Ordinal);
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
