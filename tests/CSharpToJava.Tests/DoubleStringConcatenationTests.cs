using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DoubleStringConcatenationTests
{
    [Fact]
    public void Double_ConcatenatedWithString_UsesDotNetFormatting()
    {
        var result = Convert("""
            class Sample
            {
                string M(double d)
                {
                    return "x=" + d;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("DoubleHelper.toString(d)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x=\" + d;", result.GeneratedCode, StringComparison.Ordinal);
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
