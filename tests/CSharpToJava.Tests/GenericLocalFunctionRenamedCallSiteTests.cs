using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;

namespace CSharpToJava.Tests;

/// <summary>
/// Same-named generic local functions in different methods are hoisted with
/// distinct Java names (f, f2, ...), and the generic call sites resolve to
/// CONSTRUCTED method symbols. The hoisted-name registry is keyed by the
/// original definition, so call-site lookup must normalize to the original
/// definition before consulting it; otherwise call sites fall back to the
/// base name and emit f(long.class) while the hoisted declaration is f2(...),
/// producing a javac "cannot apply method to given types" error.
/// </summary>
public class GenericLocalFunctionRenamedCallSiteTests
{
    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void SameNameGenericLocalFunctionsInDifferentMethods_CallSitesUseRenamedMethod()
    {
        var result = Convert("""
class C {
    Object A() {
        return RunTestAsync<int>(new Object());
        Object RunTestAsync<T>(Object e) => e is T ? e : null;
    }
    Object B() {
        return RunTestAsync<long>(new Object());
        Object RunTestAsync<T>(Object e) => e is T ? e : null;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        // First local function keeps the base name; second gets the numeric suffix.
        Assert.Contains("private <T> Object runTestAsync(Class<T> _cs2j_T, Object e)", code, StringComparison.Ordinal);
        Assert.Contains("private <T> Object runTestAsync2(Class<T> _cs2j_T, Object e)", code, StringComparison.Ordinal);
        // The generic call site must follow the hoisted declaration's renamed method.
        Assert.Contains("runTestAsync2(long.class, new Object())", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void GenericCalleeWithoutClassTokenSlots_DoesNotInjectSpuriousToken()
    {
        // The abstract helper Get<T> has no Class<T> slot in its Java signature
        // (abstract methods cannot gain leading Class parameters, and overrides
        // must keep the base erasure). A generic local function that forwards T to
        // such a helper must not gain a Class<T> parameter either, and the outer
        // call site must not prepend a token (previously the member-access generic
        // scan registered a spurious requirement, emitting inner(_cs2j_T, "x")
        // against `private <T> void inner(String s)`).
        var result = Convert("""
abstract class Helper {
    public abstract T Get<T>(string s);
}
class C {
    private Helper _helper;
    void M() {
        Outer<int>();
        void Outer<T>() {
            Inner<T>("x");
            void Inner<T>(string s) {
                _helper.Get<T>(s);
            }
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("private <T> void inner(String s)", code, StringComparison.Ordinal);
        Assert.Contains("private <T> void outer()", code, StringComparison.Ordinal);
        Assert.Contains("inner(\"x\");", code, StringComparison.Ordinal);
        Assert.DoesNotContain("inner(_cs2j_", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "GenericLocalFunctionRenamedCallSiteTests.cs",
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
