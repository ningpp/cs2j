using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class DateTimeOffsetParseCompatibilityTests
{
    private readonly ITestOutputHelper _out;

    public DateTimeOffsetParseCompatibilityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DateTimeOffsetParse_WithInvariantCulture_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System;
using System.Globalization;

class Sample {
    static DateTimeOffset ParseValue(string text) {
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpDateTimeOffset.parse(text)", result.GeneratedCode, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var outDir = Path.Combine(temp.Path, "classes");
        var dotnetSystemDir = Path.Combine(temp.Path, "dotnet", "system");
        var dotnetSystemStubPath = Path.Combine(dotnetSystemDir, "Placeholder.java");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dotnetSystemDir);
        File.WriteAllText(samplePath, result.GeneratedCode);
        File.WriteAllText(dotnetSystemStubPath, "package dotnet.system; public final class Placeholder { }");

        var repoRoot = FindRepoRoot();
        var compatDir = Path.Combine(repoRoot, "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat");
        var compatSources = new[]
        {
            "CSharpDateTimeOffset.java",
            "CSharpDateTime.java",
            "CSharpTimeSpan.java",
            "DateTimeKind.java",
            "DayOfWeek.java",
            "ObjectHolder.java",
        }.Select(name => Path.Combine(compatDir, name)).ToArray();

        foreach (var source in compatSources)
            Assert.True(File.Exists(source), $"Missing compat source: {source}");

        var sources = string.Join(" ", compatSources.Append(dotnetSystemStubPath).Append(samplePath).Select(path => $"\"{path}\""));
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
