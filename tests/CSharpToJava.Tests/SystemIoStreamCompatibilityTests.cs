using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class SystemIoStreamCompatibilityTests
{
    private readonly ITestOutputHelper _out;
    public SystemIoStreamCompatibilityTests(ITestOutputHelper output) { _out = output; }

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
    public void MemoryStream_StreamWriter_BaseStream_StreamReader_RoundTrip_UsesCompatTypes()
    {
        var result = Convert("""
using System.IO;
class SvgGraphWriter {
    public SvgGraphWriter(Stream stream, object graph) { }
    public void Write() { }
}
class Sample {
    static string Render(object graph) {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms);
        var svgWriter = new SvgGraphWriter(writer.BaseStream, graph);
        svgWriter.Write();
        ms.Position = 0;
        var sr = new StreamReader(ms);
        return sr.ReadToEnd();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new MemoryStream()", code);
        Assert.Contains("new StreamWriter(ms)", code);
        Assert.Contains("new SvgGraphWriter(writer.getBaseStream(), graph)", code);
        Assert.Contains("ms.setPosition(0L)", code);
        Assert.Contains("new StreamReader(ms)", code);
        Assert.Contains("sr.readToEnd()", code);

        Assert.DoesNotContain("new ByteArrayInputStream()", code);
        Assert.DoesNotContain("new BufferedReader(StreamWrapper.of(ms))", code);
        Assert.DoesNotContain("new PrintWriter(StreamWrapper.of(ms))", code);
    }
}
