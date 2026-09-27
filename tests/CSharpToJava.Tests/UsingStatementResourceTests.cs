using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class UsingStatementResourceTests
{
    private readonly ITestOutputHelper _out;
    public UsingStatementResourceTests(ITestOutputHelper output) { _out = output; }

    private static ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void UsingExistingLocal_EmitsResourceReference()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(Stream stream) {
        using (stream) {
            return stream.ReadByte();
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (stream)", code);
        Assert.DoesNotContain("try ()", code);
    }

}
