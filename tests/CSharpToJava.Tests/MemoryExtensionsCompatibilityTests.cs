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
    public void SpanEqualityOperator_UsesSequenceEqual()
    {
        // C# Span<T> == / != are value comparisons (contents + length), but the
        // Java port maps spans to classes where == is reference identity.
        var result = Convert("""
using System;
class C {
    static bool IsEmpty(System.ReadOnlySpan<byte> span) {
        return span == default;
    }
    static bool NonEmpty(System.ReadOnlySpan<byte> span) {
        return span != default;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("MemoryExtensions.sequenceEqual(", code);
        Assert.DoesNotContain("span == null", code);
        Assert.DoesNotContain("span != null", code);
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
