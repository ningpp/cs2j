using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class LocalFunctionHoistingTests
{
    [Fact]
    public void NoCaptureLocalFunctions_HoistToPrivateClassMethods()
    {
        // RegexNode.ReduceAlternation contains local functions called before their
        // declaration. Java has no local functions, so the converter must hoist them
        // to class-level private methods with the same camelCase names as call sites.
        var result = Convert("""
class C {
    int M(int x) {
        return Square(x) + DoubleIt(x);
        int Square(int v) => v * v;
        int DoubleIt(int v) { return v * 2; }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("private int square(int v)", code, StringComparison.Ordinal);
        Assert.Contains("private int doubleIt(int v)", code, StringComparison.Ordinal);
        Assert.Contains("square(x)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: LocalFunctionStatement", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact]
    public void LocalFunctionAfterReturn_AndNestedStaticLocalFunction_AreHoisted()
    {
        // The nested static local function Len is declared after a return statement,
        // so the hoisting pre-scan must find it, not the statement walk.
        var result = Convert("""
class C {
    int M(string[] items) {
        return Sum(items);
        int Sum(string[] arr) {
            int total = 0;
            foreach (string it in arr) total += Len(it);
            return total;
            static int Len(string s) => s.Length;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("private int sum(String[] arr)", code, StringComparison.Ordinal);
        Assert.Contains("private static int len(String s)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: LocalFunctionStatement", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact]
    public void CapturingLocalFunction_KeepsTodoFallback()
    {
        // Local functions that capture enclosing locals cannot be hoisted without
        // closure conversion.
        var result = Convert("""
class C {
    int M(int x) {
        return Twice();
        int Twice() { return x * 2; }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The declaration stays un-hoisted (closure conversion is not implemented yet),
        // and the call site becomes a visible TODO comment instead of a broken method call.
        Assert.Contains("TODO: call to capturing local function Twice", result.GeneratedCode ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("private int twice", result.GeneratedCode ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("twice()", result.GeneratedCode ?? "", StringComparison.Ordinal);
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

    private static void CompileJava(string code)
    {
        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "C.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);
        var javac = RunProcess("javac", $"-d \"{outDir}\" \"{samplePath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
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
