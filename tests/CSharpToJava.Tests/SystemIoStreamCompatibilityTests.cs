using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
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

    [Fact]
    public void TextReader_BlockRead_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System.IO;

class Sample {
    static int Fill(TextReader reader, char[] chars, int used) {
        return reader.Read(chars, used, chars.Length - used - 1);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("reader.read(chars, used, chars.length - used - 1)", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);

        var repoRoot = FindRepoRoot();
        var textReaderPath = Path.Combine(repoRoot, "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat", "TextReader.java");
        Assert.True(File.Exists(textReaderPath), $"Missing compat source: {textReaderPath}");

        var javac = RunProcess("javac", $"-d \"{outDir}\" \"{textReaderPath}\" \"{samplePath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    [Fact]
    public void StringReaderVariableInitialization_RemainsJavaStringReader()
    {
        var result = Convert("""
using System.IO;

class Sample {
    static int Read(string text) {
        StringReader sr = new StringReader(text);
        return sr.Read();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("StringReader sr = new StringReader(text)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StringReader sr = new TextReader", code, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlReaderCreate_WithStringReader_WrapsArgumentAsCompatTextReader()
    {
        var result = Convert("""
using System.IO;
using System.Xml;

class Sample {
    static XmlReader Read(string text) {
        StringReader sr = new StringReader(text);
        return XmlReader.Create(sr);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("StringReader sr = new StringReader(text)", code, StringComparison.Ordinal);
        Assert.Contains("XmlReader.create(new TextReader(sr))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("XmlReader.create(sr)", code, StringComparison.Ordinal);
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

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
