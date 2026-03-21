using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class BufferedReaderStringCtorTests
{
    [Fact]
    public void BufferedReader_WithStringPath_WrapsFileReader()
    {
        const string code = """
            using System.IO;

            public class C {
                void M(string path) {
                    var r = new StreamReader(path);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new BufferedReader(new FileReader(path))", result.GeneratedCode);
    }
}
