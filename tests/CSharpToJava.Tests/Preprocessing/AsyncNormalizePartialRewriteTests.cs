using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C18 node-level isolation: a file that contains an un-normalizable node such as a
/// user-written <c>async void</c> member (CS2J1803) must not block independent,
/// safely liftable async lambdas. <see cref="AsyncNormalizeOptions.AllowPartialRewriteOnError"/>
/// opens only the already-planned lambda/local-function lifting; the default
/// all-or-nothing behavior is byte-for-byte preserved.
/// </summary>
public sealed class AsyncNormalizePartialRewriteTests
{
    // ── 1. Baseline: async void keeps the whole file untouched ──

    [Fact]
    public void AsyncVoid_WithZeroCaptureLambda_DefaultMode_LeavesFileUntouched()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async void Fire()
                {
                    await Task.Delay(1);
                }

                public async Task Run()
                {
                    Func<Task> f = async () => { await Task.Delay(1); };
                    await f();
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1803);
        Assert.True(result.Statistics.ResidualCount > 0);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedInPartialRewrite);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
    }

    // ── 2. Partial mode lifts the independent zero-capture lambda ──

    [Fact]
    public void AsyncVoid_WithZeroCaptureLambda_PartialMode_LiftsLambdaAndKeepsDiagnostic()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async void Fire()
                {
                    await Task.Delay(1);
                }

                public async Task Run()
                {
                    Func<Task> f = async () => { await Task.Delay(1); };
                    await f();
                }
            }
            """;
        var result = Run(src, partial: true);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.DoesNotContain("async ()", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedInPartialRewrite);

        // The async void node stays: partial rewrite does not waive its diagnostic
        // and the residual gate still reports the remaining async void member.
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1803);
        Assert.True(result.Statistics.ResidualCount > 0);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. Partial mode also lifts a safely-capturing async lambda ──

    [Fact]
    public void AsyncVoid_WithCapturedLambda_PartialMode_LiftsCapturedLambda()
    {
        const string src = """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                public async void Fire()
                {
                    await Task.Delay(1);
                }

                public async Task Run()
                {
                    Stream stream = new MemoryStream();
                    Func<Task> f = async () => { await stream.FlushAsync(); };
                    await f();
                }
            }
            """;
        var result = Run(src, partial: true);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task " +
            "Cs2jAsync_Run_0(global::System.IO.Stream stream)",
            result.OutputCode);
        Assert.Contains("() => Cs2jAsync_Run_0(stream)", result.OutputCode);
        Assert.DoesNotContain("async ()", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedCapturedLocals);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedInPartialRewrite);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1803);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── Helpers (TPA references + Latest, matching the existing C18 fixtures) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static AsyncNormalizeFileResult Run(string src, bool partial = false)
    {
        var model = ParseWithSemantics(src);
        var options = partial
            ? new AsyncNormalizeOptions(AllowPartialRewriteOnError: true)
            : new AsyncNormalizeOptions();
        return new AsyncNormalizePreprocessor().Preprocess(src, model, options);
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
            "async-normalize-partial-output",
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
            "async-normalize-partial-test",
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
