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
