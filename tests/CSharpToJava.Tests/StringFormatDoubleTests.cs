using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringFormatDoubleTests
{
    [Fact]
    public void Format_DoubleArguments_UseDotNetFormatting()
    {
        var result = Convert("""
            class Sample
            {
                string M(double a, double b)
                {
                    return string.Format("{0},{1}", a, b);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("DoubleHelper.toString(a)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("DoubleHelper.toString(b)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.format(\"%1$s,%2$s\", a, b)", result.GeneratedCode, StringComparison.Ordinal);
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
