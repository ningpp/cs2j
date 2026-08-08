using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class MemoryExtensionsCompatibilityTests
{
    private readonly ITestOutputHelper _out;

    public MemoryExtensionsCompatibilityTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SequenceEqualSpans_CompilesAgainstCompatRuntime()
    {
        // RegexBoyerMoore.cs:254 uses Pattern.AsSpan().SequenceEqual(text.AsSpan(...)).
        // The converter emits MemoryExtensions.sequenceEqual(ReadOnlySpan<Character>,
        // ReadOnlySpan<Character>), which must exist in the compat MemoryExtensions.
        var result = Convert("""
using System;
class C {
    static bool EqualsSegment(string a, string b) {
        return a.AsSpan().SequenceEqual(b.AsSpan(1, 3));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MemoryExtensions.sequenceEqual(", code);


        // Mirror CrossPackageImportResolver (IR path): helper-class imports are rewritten
        // to the shared compatibility package in full project conversions.
        code = code.Replace(
            "import dotnet.system.MemoryExtensions;",
            "import io.github.ningpp.compat.MemoryExtensions;",
            StringComparison.Ordinal);
        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "C.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);

        var compatDir = Path.Combine(FindRepoRoot(), "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat");
        var compatSources = new[]
        {
            "MemoryExtensions.java",
            "ReadOnlySpan.java",
            "ReadOnlyMemory.java",
            "Span.java",
            "CultureInfo.java",
            "IFormatProvider.java",
            "TextInfo.java",
            "NumberFormatInfo.java",
            "DateTimeFormatInfo.java",
            "CompareInfo.java",
            "CultureTypes.java",
        }.Select(name => Path.Combine(compatDir, name)).ToArray();

        foreach (var source in compatSources)
            Assert.True(File.Exists(source), $"Missing compat source: {source}");

        var sources = string.Join(" ", compatSources.Append(samplePath).Select(path => $"\"{path}\""));
        var javac = RunProcess("javac", $"-d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var sourceFiles = new List<SourceFile>
        {
            new SourceFile { FilePath = "Sample.cs", Content = sourceCode }
        };
        var results = pipeline.ConvertProjectAsync(sourceFiles,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFullPath("Sample.cs") }).GetAwaiter().GetResult();
        var result = results.FirstOrDefault(r => r.FileName?.Contains("Sample") == true) ?? results.Last();
        return new ConversionResult
        {
            Success = result.Success,
            Diagnostics = result.Diagnostics,
            GeneratedCode = result.GeneratedCode,
        };
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult());
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
