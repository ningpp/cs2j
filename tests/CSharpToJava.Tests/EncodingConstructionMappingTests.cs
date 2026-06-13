using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// When new UTF8Encoding(...) or new ASCIIEncoding() is used, the converter
/// should generate Encoding.getUTF8() / Encoding.getASCII() instead of
/// StandardCharsets.UTF_8 / StandardCharsets.US_ASCII, because the compat
/// Encoding type wraps the Charset and is needed for type compatibility.
/// </summary>
public class EncodingConstructionMappingTests
{
    private readonly ITestOutputHelper _out;
    public EncodingConstructionMappingTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
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
    public void NewUTF8Encoding_ProducesEncodingGetUTF8()
    {
        var r = Convert(@"
using System.Text;

class Sample
{
    Encoding DetectEncoding()
    {
        return new UTF8Encoding(true, true);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should generate Encoding.getUTF8() not StandardCharsets.UTF_8
        Assert.Contains("Encoding.getUTF8()", code);
        Assert.DoesNotContain("StandardCharsets", code);
        Assert.DoesNotContain("Charset", code);
    }

    [Fact]
    public void NewASCIIEncoding_ProducesEncodingGetASCII()
    {
        var r = Convert(@"
using System.Text;

class Sample
{
    Encoding GetAscii()
    {
        return new ASCIIEncoding();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("Encoding.getASCII()", code);
        Assert.DoesNotContain("StandardCharsets", code);
    }
}
