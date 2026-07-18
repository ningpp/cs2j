using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TextWriterMappingTests
{
    [Fact]
    public void ClassExtendingTextWriter_WithParameterlessCtor_MapsToCSharpTextWriter()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
            using System.IO;

            public class CustomWriter : TextWriter
            {
                public CustomWriter()
                {
                }

                public override void Write(char value)
                {
                    System.Console.Write(value);
                }

                public void WriteLineWithNewLine()
                {
                    Write(this.NewLine);
                }
            }
            """,
            FileName = "CustomWriter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("extends PrintWriter", code, StringComparison.Ordinal);
        Assert.Contains("extends CSharpTextWriter", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpTextWriter;", code, StringComparison.Ordinal);
        Assert.Contains("this.getNewLine()", code, StringComparison.Ordinal);
    }
}
