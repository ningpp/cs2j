using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# string.AsSpan(start) maps to MemoryExtensions.asSpan(String, int), which must
/// exist in the compat MemoryExtensions (slice from start to end). RegexCharClass
/// uses state.set.AsSpan(2).CopyTo(...); without the 2-arg overload javac reports
/// "cannot apply asSpan to given types; found String,int".
/// </summary>
public class MemoryExtensionsAsSpanStartTests
{
    private readonly ITestOutputHelper _out;

    public MemoryExtensionsAsSpanStartTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void StringAsSpanStart_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System;
class C {
    static void Build(string set, System.ReadOnlySpan<char> span) {
        set.AsSpan(2).CopyTo(span.Slice(0));
        set.AsSpan().CopyTo(span.Slice(0));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MemoryExtensions.asSpan(set, 2)", code);

        using var temp = new TempDir();
        // The generated code binds MemoryExtensions to the project-local class
        // (dotnet.system.MemoryExtensions), so verify the compat API surface with a
        // direct API check instead of compiling the full generated file.
        var samplePath = Path.Combine(temp.Path, "CompatApiCheck.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, """
import io.github.ningpp.compat.MemoryExtensions;
import io.github.ningpp.compat.Span;
class CompatApiCheck {
    static void check(String set, Span<Character> span) {
        MemoryExtensions.asSpan(set, 2).copyTo(span.slice(0));
        MemoryExtensions.asSpan(set).copyTo(span.slice(0));
    }
}
""");

        var compatDir = Path.Combine(FindRepoRoot(), "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat");
        var compatSources = new[]
        {
            "MemoryExtensions.java",
            "ReadOnlyMemory.java",
            "ReadOnlySpan.java",
            "Span.java",
        }.Select(name => Path.Combine(compatDir, name)).ToArray();

        foreach (var source in compatSources)
            Assert.True(File.Exists(source), $"Missing compat source: {source}");

        var sources = string.Join(" ", compatSources.Append(samplePath).Select(path => $"\"{path}\""));
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
