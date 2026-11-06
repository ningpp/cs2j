using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C18 ConfigureAwait(ConfigureAwaitOptions) 语义等价归一化：
/// 独立 await 表达式语句上的 options 形态改写为 try/catch 吞异常或直接剥离为
/// ConfigureAwait(false)，非独立 await（结果被使用、嵌套在表达式中）仍报 CS2J1808。
/// 自定义同名扩展不得误伤；归一化后 residual 闸门必须归零。
/// </summary>
public sealed class AsyncNormalizeConfigureAwaitOptionsTests
{
    // ── 1. 独立 await + SuppressThrowing → try/catch 包裹 ──

    [Fact]
    public void StandaloneAwait_SuppressThrowing_RewritesToTryCatch()
    {
        const string src = """
            using System.Threading.Tasks;
            class C
            {
                public async Task M(Task pendingTask)
                {
                    await pendingTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("try", result.OutputCode);
        Assert.Contains("catch", result.OutputCode);
        Assert.Contains("await pendingTask;", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. 结果被使用 → 非独立 await，保持 CS2J1808 ──

    [Fact]
    public void ResultUsed_SuppressThrowing_RemainsError()
    {
        const string src = """
            using System.Threading.Tasks;
            class C
            {
                public async Task M(Task<int> pendingTask)
                {
                    int value = await pendingTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.Contains("ConfigureAwait", result.OutputCode);
    }

    // ── 3. 独立 await + 非 SuppressThrowing → 剥离为 ConfigureAwait(false) ──

    [Fact]
    public void StandaloneAwait_ContinueOnCapturedContext_StripsOptions()
    {
        const string src = """
            using System.Threading.Tasks;
            class C
            {
                public async Task M(Task pendingTask)
                {
                    await pendingTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.DoesNotContain("catch", result.OutputCode);
        Assert.Contains("await pendingTask;", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 4. 自定义同名扩展（receiver 非 Task）→ 不改写 ──

    [Fact]
    public void CustomSameNameExtension_NotRewritten()
    {
        const string src = """
            using System.Threading.Tasks;
            class Foo
            {
                public Task GetTask() => Task.CompletedTask;
            }
            static class FooExtensions
            {
                public static Task ConfigureAwait(this Foo foo, ConfigureAwaitOptions options)
                    => Task.CompletedTask;
            }
            class C
            {
                public async Task M()
                {
                    var foo = new Foo();
                    await foo.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ConfigureAwait", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.Equal(0, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    // ── Helpers (TPA references + Latest, matching the existing C18 fixtures) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static AsyncNormalizeFileResult Run(string src)
    {
        var model = ParseWithSemantics(src);
        return new AsyncNormalizePreprocessor().Preprocess(src, model);
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
            "async-normalize-configureawait-options-output",
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
            "async-normalize-configureawait-options-test",
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
