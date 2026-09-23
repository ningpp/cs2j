using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Interlocked.Or(ref int, int) is emitted as Interlocked.or(IntHolder, int)
/// (ref-holder pattern). The compat Interlocked must expose this overload;
/// without it javac reports "cannot find symbol: method or(IntHolder,int)".
/// Regression: RegexCharClass setSlot uses Interlocked.Or(ref slot, bitsToSet).
/// </summary>
public class InterlockedOrCompatTests
{
    private readonly ITestOutputHelper _out;

    public InterlockedOrCompatTests(ITestOutputHelper output) => _out = output;

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
