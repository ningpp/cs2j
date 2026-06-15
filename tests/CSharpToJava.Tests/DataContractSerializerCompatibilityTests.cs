using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DataContractSerializerCompatibilityTests
{
    private readonly ITestOutputHelper _out;

    public DataContractSerializerCompatibilityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ReadObjectWithVerifyObjectName_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System.Runtime.Serialization;
using System.Xml;

class Sample {
    static object Read(XmlReader xr) {
        var dcs = new DataContractSerializer(typeof(object));
        return dcs.ReadObject(xr, true);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("dcs.readObject(xr, true)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.DataContractSerializer;", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var outDir = Path.Combine(temp.Path, "classes");
        var dotnetSystemDir = Path.Combine(temp.Path, "dotnet", "system");
        var dotnetXmlDir = Path.Combine(temp.Path, "dotnet", "xml");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dotnetSystemDir);
        Directory.CreateDirectory(dotnetXmlDir);

        var dotnetSystemStubPath = Path.Combine(dotnetSystemDir, "Placeholder.java");
        var xmlReaderStubPath = Path.Combine(dotnetXmlDir, "XmlReader.java");
        File.WriteAllText(samplePath, code);
        File.WriteAllText(dotnetSystemStubPath, "package dotnet.system; public final class Placeholder { }");
        File.WriteAllText(xmlReaderStubPath, "package dotnet.xml; public class XmlReader { }");

        var repoRoot = FindRepoRoot();
        var serializerPath = Path.Combine(
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
            "DataContractSerializer.java");

        Assert.True(File.Exists(serializerPath), $"Missing compat source: {serializerPath}");

        var sources = string.Join(" ", new[] { serializerPath, dotnetSystemStubPath, xmlReaderStubPath, samplePath }.Select(path => $"\"{path}\""));
        var javac = RunProcess("javac", $"-d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    [Fact]
    public void WriteObject_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System.Runtime.Serialization;
using System.Xml;

class Sample {
    static void Write(XmlWriter xw, object value) {
        var dcs = new DataContractSerializer(value.GetType());
        dcs.WriteObject(xw, value);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("dcs.writeObject(xw, value)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.DataContractSerializer;", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var outDir = Path.Combine(temp.Path, "classes");
        var dotnetSystemDir = Path.Combine(temp.Path, "dotnet", "system");
        var dotnetXmlDir = Path.Combine(temp.Path, "dotnet", "xml");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dotnetSystemDir);
        Directory.CreateDirectory(dotnetXmlDir);

        var dotnetSystemStubPath = Path.Combine(dotnetSystemDir, "Placeholder.java");
        var xmlWriterStubPath = Path.Combine(dotnetXmlDir, "XmlWriter.java");
        File.WriteAllText(samplePath, code);
        File.WriteAllText(dotnetSystemStubPath, "package dotnet.system; public final class Placeholder { }");
        File.WriteAllText(xmlWriterStubPath, "package dotnet.xml; public class XmlWriter { }");

        var serializerPath = GetDataContractSerializerPath();
        var sources = string.Join(" ", new[] { serializerPath, dotnetSystemStubPath, xmlWriterStubPath, samplePath }.Select(path => $"\"{path}\""));
        var javac = RunProcess("javac", $"-d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);
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

    private static string GetDataContractSerializerPath()
    {
        var serializerPath = Path.Combine(
            FindRepoRoot(),
            "java",
            "csharptojava-compat",
            "src",
            "main",
            "java",
            "io",
            "github",
            "ningpp",
            "compat",
            "DataContractSerializer.java");

        Assert.True(File.Exists(serializerPath), $"Missing compat source: {serializerPath}");
        return serializerPath;
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
