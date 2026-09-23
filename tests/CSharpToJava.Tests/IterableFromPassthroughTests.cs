using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace CSharpToJava.Tests;

public class IterableFromPassthroughTests
{

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
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
        using var proc = Process.Start(psi)!;
        var sb = new StringBuilder();
        sb.Append(proc.StandardOutput.ReadToEnd());
        sb.Append(proc.StandardError.ReadToEnd());
        proc.WaitForExit();
        return (proc.ExitCode, sb.ToString());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir() => Directory.CreateDirectory(Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j_" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
