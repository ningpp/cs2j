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

    [Fact]
    public void EncodingPreambleProperty_WrapsByteArrayAsReadOnlySpan()
    {
        var r = Convert(@"
using System;
using System.Text;

class Sample
{
    void M(Encoding enc)
    {
        ReadOnlySpan<byte> preamble = enc.Preamble;
        int len = preamble.Length;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("ReadOnlySpan<Integer> preamble = MemoryExtensions.asSpan(enc.getPreamble())", code);
        Assert.DoesNotContain("ReadOnlySpan<Integer> preamble = enc.getPreamble()", code);
        Assert.Contains("import io.github.ningpp.compat.MemoryExtensions", code);
    }

    [Fact]
    public void EncodingGetPreambleOverride_KeepsByteArrayReturnType()
    {
        var r = Convert(@"
using System.Text;

abstract class CustomEncoding : Encoding
{
    public override byte[] GetPreamble()
    {
        return new byte[] { 0xEF, 0xBB, 0xBF };
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("public byte[] getPreamble()", code);
        Assert.DoesNotContain("public byte[] GetPreamble()", code);
    }

    [Fact]
    public void EncodingPreambleOverride_DoesNotCollideWithGetPreambleMethod()
    {
        var r = Convert(@"
using System;
using System.Text;

abstract class CustomEncoding : Encoding
{
    private static readonly byte[] s_preamble = new byte[] { 0xEF, 0xBB, 0xBF };

    public override ReadOnlySpan<byte> Preamble => s_preamble;
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("public ReadOnlySpan<Integer> getPreambleSpan()", code);
        Assert.Contains("return MemoryExtensions.asSpan(s_preamble);", code);
        Assert.DoesNotContain("public ReadOnlySpan<Integer> getPreamble()", code);
    }

    [Fact]
    public void CompatEncodingGetEncoding_AcceptsCustomEncoderFallback()
    {
        var repoRoot = FindRepoRoot();
        var encodingPath = Path.Combine(
            repoRoot,
            "java",
            "csharptojava-compat",
            "src",
            "main",
            "java",
            "io",
            "github",
            "ningpp",
            "compat",
            "Encoding.java");

        var source = File.ReadAllText(encodingPath);

        Assert.Contains(
            "public static Encoding getEncoding(int codePage, EncoderFallback encoderFallback, DecoderReplacementFallback decoderFallback)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "getEncoding(int codePage, EncoderReplacementFallback encoderFallback, DecoderReplacementFallback decoderFallback)",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
