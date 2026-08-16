using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringWriterWriteLineTests
{
    [Fact]
    public void WriteLine_UsesPlatformLineSeparator()
    {
        var result = Convert("""
            using System.IO;

            class Sample
            {
                string M()
                {
                    var sw = new StringWriter();
                    sw.WriteLine("line");
                    sw.Close();
                    return sw.ToString();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("System.lineSeparator()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("+ \"\\n\")", result.GeneratedCode, StringComparison.Ordinal);
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
