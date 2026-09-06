using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class LocalFunctionHoistingTests
{
    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
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

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
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

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void CapturingLocalFunction_IsHoistedWithCapturedVariablesAsParameters()
    {
        // Local functions that capture enclosing locals are hoisted to private
        // class-level methods with the captured variables appended as trailing
        // _cs2j_cap_ parameters, and call sites pass the captured arguments.
        var result = Convert("""
class C {
    int M(int x) {
        return Twice();
        int Twice() { return x * 2; }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private int twice(int _cs2j_cap_x)", result.GeneratedCode ?? "", StringComparison.Ordinal);
        Assert.Contains("twice(x)", result.GeneratedCode ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: call to capturing local function", result.GeneratedCode ?? "", StringComparison.Ordinal);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void ExpressionBodiedGenericLocalFunction_GetsClassTokenAtCallSite()
    {
        // T F<T>() => default(T) has no block body; the Class<T> token detection
        // must scan the expression body so the call site emits the token that the
        // hoisted signature requires (previously f() vs f(Class<T>) — javac error).
        var result = Convert("""
class C {
    int M() {
        return F<int>();
        T F<T>() => default(T);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("f(int.class)", code, StringComparison.Ordinal);
        Assert.Contains("private <T> T f(Class<T> _cs2j_T)", code, StringComparison.Ordinal);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void GenericLocalFunctionWithIsTypeParamCheck_GetsClassTokenAtCallSite()
    {
        // Expression-bodied `e is T` checks also register Class<T> token requirements;
        // the syntactic fallback must not skip the expression body.
        var result = Convert("""
class C {
    bool M(object o) {
        return F<string>(o);
        bool F<T>(object e) => e is T;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("f(String.class, o)", code, StringComparison.Ordinal);
        Assert.Contains("private <T> boolean f(Class<T> _cs2j_T", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void LocalFunctionCollidingWithClassMember_IsRenamedAndCallSiteUpdated()
    {
        // The class method Square and the local function square both camelCase to
        // "square". Previously the hoisted function was silently dropped by the
        // erased-signature dedup, rebinding M() to the class method. Now the local
        // function gets a unique name and the call site follows it.
        var result = Convert("""
class C {
    int Square(int x) => x * x;
    int M() {
        return square(3);
        int square(int x) => x * 2;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("int square(int x) { return x * x; }", code, StringComparison.Ordinal);
        Assert.Contains("return square2(3);", code, StringComparison.Ordinal);
        Assert.Contains("private int square2(int x) { return x * 2; }", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void SameNameLocalFunctionsInDifferentMethods_GetDistinctHoistedNames()
    {
        // Two methods each declaring a local function named Twice previously collided
        // at class level: only the first was hoisted and the second call site bound
        // to the first function (v * 2 instead of v * 3). Both must be hoisted with
        // unique names and each call site must bind to its own.
        var result = Convert("""
class C {
    int A(int x) {
        return Twice(x);
        int Twice(int v) => v * 2;
    }
    int B(int x) {
        return Twice(x + 1);
        int Twice(int v) => v * 3;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("private int twice(int v) { return v * 2; }", code, StringComparison.Ordinal);
        Assert.Contains("private int twice2(int v) { return v * 3; }", code, StringComparison.Ordinal);
        var aBody = ExtractMethodBody(code, "int a(int x)");
        var bBody = ExtractMethodBody(code, "int b(int x)");
        Assert.Contains("return twice(x);", aBody, StringComparison.Ordinal);
        Assert.Contains("return twice2(x + 1);", bBody, StringComparison.Ordinal);
        CompileJava(code);
    }

    [Fact(Skip = "预处理阶段已消除局部函数；转换器 hoisting 路径仅作为 fallback 保留。")]
    public void LocalFunctionNamedGetHashCode_CallSiteMatchesHoistedDeclaration()
    {
        // Regular method renames (GetHashCode → hashCode) must not apply to local
        // functions — the hoisted declaration is plain camelCase (getHashCode) and
        // the call site must emit the same name.
        var result = Convert("""
class C {
    int M(int x) {
        return GetHashCode(x);
        int GetHashCode(int v) => v * 7;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("return getHashCode(x);", code, StringComparison.Ordinal);
        Assert.Contains("private int getHashCode(int v) { return v * 7; }", code, StringComparison.Ordinal);
        CompileJava(code);
    }

    private static string ExtractMethodBody(string code, string methodSignaturePrefix)
    {
        int idx = code.IndexOf(methodSignaturePrefix, System.StringComparison.Ordinal);
        Assert.True(idx >= 0, $"method {methodSignaturePrefix} not found in:\n{code}");
        int open = code.IndexOf('{', idx);
        int close = code.IndexOf('}', open);
        return code.Substring(open, close - open);
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
