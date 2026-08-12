using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# System.Windows.Forms.Cursor(Stream) maps to dotnet.winforms.swing.Cursor.
/// The converter maps C# System.IO.Stream to compat StreamWrapper, so the compat
/// Cursor constructor must accept StreamWrapper (not java.io.InputStream).
/// Regression: GViewer uses new Cursor(Assembly.GetManifestResourceStream(r)).
/// </summary>
public class CursorStreamCompatTests
{
    private readonly ITestOutputHelper _out;

    public CursorStreamCompatTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CursorFromManifestResourceStream_CompilesAgainstCompatRuntime()
    {
        var result = Convert("""
using System.Windows.Forms;
using System.Reflection;

class Sample {
    static Cursor Load(Assembly a, string r) {
        return new Cursor(a.GetManifestResourceStream(r));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new Cursor(a.getManifestResourceStream(r))", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "Sample.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);

        var compatClasses = Path.GetFullPath(Path.Combine(FindRepoRoot(), "java", "csharptojava-compat", "target", "classes"));
        Assert.True(Directory.Exists(compatClasses), $"Missing compat build output: {compatClasses}");

        var javac = RunProcess("javac", $"-cp \"{compatClasses}\" -d \"{outDir}\" \"{samplePath}\"");
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
