using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class JavaDocCommentEscapeTests
{
    [Fact]
    public void XmlDocComment_WithBlockTerminator_EscapesTerminatorInJavadoc()
    {
        var result = Convert("""
            public class C
            {
                /// <summary>
                /// local-name(child::*/following::*[last()])
                /// </summary>
                public void M()
                {
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("child::* /following::*", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("child::*/following::*", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingComment_WithUnicodeEscapeText_EscapesBackslashU()
    {
        var result = Convert("""
            public class C
            {
                public int M(int idx)
                {
                    idx += 3; // Skip the next \u sequence
                    return idx;
                }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        // Java processes \uXXXX escapes even inside comments, so \u must be escaped.
        Assert.Contains("\\u sequence", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("next \\u sequence", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingComment_WithBlockTerminator_IsPreserved()
    {
        var result = Convert("""
            public class C
            {
                public int M(int x)
                {
                    return x; // end */
            }
            }
            """);

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics)}");
        Assert.Contains("end */", result.GeneratedCode, StringComparison.Ordinal);
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
