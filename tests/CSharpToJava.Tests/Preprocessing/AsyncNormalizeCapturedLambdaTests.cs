using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C18 D3(d) safe-capture lifting: an async lambda that captures outer
/// locals/parameters/range variables is lifted to a named <c>private static async</c>
/// method taking the captured values as leading parameters, and the call site
/// becomes a non-async wrapper lambda. Anything that cannot be proven equivalent
/// (value-type captures, writes to the capture, this-capture, expression trees)
/// stays deferred with CS2J1802.
/// </summary>
public sealed class AsyncNormalizeCapturedLambdaTests
{
    // ── 1. Zero-arg Func<Task> capturing a Stream: static method + wrapper lambda ──

    [Fact]
    public void CapturedLocal_ZeroArgFuncTask_LiftsToStaticMethodWithCaptureParameter()
    {
        const string src = """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                public async Task Run()
                {
                    Stream stream = new MemoryStream();
                    Func<Task> f = async () => { await stream.FlushAsync(); };
                    await f();
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task " +
            "Cs2jAsync_Run_0(global::System.IO.Stream stream)",
            result.OutputCode);
        Assert.Contains("() => Cs2jAsync_Run_0(stream)", result.OutputCode);
        // The call site is a non-async wrapper lambda; the original async lambda is gone.
        Assert.DoesNotContain("async ()", result.OutputCode);
        Assert.DoesNotContain("async (", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedThisOnly);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. Func<int, Task> capturing a local and keeping its own parameter ──

    [Fact]
    public void CapturedLocal_WithLambdaParameter_BothAppearInLiftedSignatureAndWrapper()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async Task Run(int seed)
                {
                    string prefix = "p";
                    Func<int, Task> f = async n =>
                    {
                        await Task.Delay(n);
                        Console.WriteLine(prefix);
                    };
                    await f(seed);
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task " +
            "Cs2jAsync_Run_0(string prefix, int n)",
            result.OutputCode);
        // Wrapper keeps the original lambda parameter list and forwards both the
        // captured value and the lambda argument.
        Assert.Contains("(int n) => Cs2jAsync_Run_0(prefix, n)", result.OutputCode);
        Assert.DoesNotContain("async (", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. Negative: a capture reassigned after the lambda is created stays deferred ──

    [Fact]
    public void CapturedLocal_WrittenAfterLambdaCreation_DefersWith1802()
    {
        const string src = """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                public async Task Run()
                {
                    Stream stream = new MemoryStream();
                    Func<Task> f = async () => { await stream.FlushAsync(); };
                    stream = new MemoryStream();
                    await f();
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.Equal(1, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.True(result.Statistics.ResidualCount > 0);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
    }

    // ── 4. Negative: value-type captures are never lifted (no reference snapshot) ──

    [Fact]
    public void CapturedLocal_ValueType_DefersWith1802()
    {
        const string src = """
            using System.Threading.Tasks;
            class C
            {
                public async Task Run()
                {
                    int state = 1;
                    System.Func<System.Threading.Tasks.Task> f = async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(state);
                    };
                    await f();
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.Equal(1, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.True(result.Statistics.ResidualCount > 0);
    }

    // ── 5. Negative: this + local capture together stays deferred ──

    [Fact]
    public void CapturedLocal_WithThisCapture_DefersWith1802()
    {
        const string src = """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                private int _count;

                public async Task Run()
                {
                    Stream stream = new MemoryStream();
                    Func<Task> f = async () =>
                    {
                        await stream.FlushAsync();
                        _count++;
                    };
                    await f();
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.Equal(1, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.True(result.Statistics.ResidualCount > 0);
    }

    // ── 6. Negative: async lambda inside an expression tree is never lifted ──

    [Fact]
    public void ExpressionTreeAsyncLambda_IsNotLifted()
    {
        const string src = """
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;
            class C
            {
                public void Run()
                {
                    Expression<Func<Task>> e = async () => await Task.CompletedTask;
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.True(result.Statistics.ResidualCount > 0);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
    }

    // ── 7. Zero-capture behavior does not regress to the capturing path ──

    [Fact]
    public void ZeroCaptureLambda_StillUsesDelegateConstruction()
    {
        const string src = """
            using System.Threading.Tasks;
            class C
            {
                public async Task Run()
                {
                    await Task.Run(async () => await Task.CompletedTask);
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task>(Cs2jAsync_Run_0)",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── Helpers (TPA references + Latest, matching the existing C18 fixtures) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static AsyncNormalizeFileResult Run(string src)
    {
        var model = ParseWithSemantics(src);
        return new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());
    }

    private static void AssertOutputParsesClean(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "rewritten output has parse errors: " +
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-normalize-captured-output",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "rewritten output has compile errors: " +
            string.Join("; ", errors.Select(e => e.ToString())));
    }

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-normalize-captured-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
