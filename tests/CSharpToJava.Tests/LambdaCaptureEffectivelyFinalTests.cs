using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: lambdas capturing Java non-effectively-final variables.
///
/// C# closures may capture (1) for-each loop variables and (2) locals/parameters
/// reassigned before the lambda. Java requires captured variables to be effectively
/// final, so javac reports:
///   "local variables referenced from a lambda expression must be final or effectively final"
///
/// Expected Java output: the captured variable is hoisted into a one-element holder
/// array (String[] _input = { input }; / int[] _options = { options; }) emitted at the
/// method body start (parameters) or the loop body top (for-each variables), and all
/// references — including the reassignments themselves — read/write the holder element.
/// </summary>
public class LambdaCaptureEffectivelyFinalTests
{
    private readonly ITestOutputHelper _out;

    public LambdaCaptureEffectivelyFinalTests(ITestOutputHelper output) => _out = output;

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
