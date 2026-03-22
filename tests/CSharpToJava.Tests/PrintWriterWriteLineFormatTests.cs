using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PrintWriterWriteLineFormatTests
{
    [Fact]
    public void StreamWriterWriteLine_FormatOverload_UsesStringFormatInsidePrintln()
    {
        const string code = """
            using System.IO;

            public class C
            {
                void M(StreamWriter writer, int n)
                {
                    writer.WriteLine("Value {0}", n);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("writer.println(String.format(\"Value %s\", n))", result.GeneratedCode);
        Assert.DoesNotContain("writer.println(\"Value {0}\", n)", result.GeneratedCode);
    }
}