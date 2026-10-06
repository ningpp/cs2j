using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class AsyncNormalizeUnitTests
{
    // ── 1. Options ──

    [Fact]
    public void Options_Defaults()
    {
        var options = new AsyncNormalizeOptions();

        Assert.True(options.Strict);
        Assert.Equal(AsyncNormalizeMode.Lightweight, options.Mode);
        Assert.True(options.LiftAsyncLambdas);
        Assert.True(options.StripConfigureAwait);
        Assert.True(options.RejectAsyncVoid);
    }

    [Fact]
    public void Options_Heavyweight_EntryThrowsNotSupported()
    {
        var preprocessor = new AsyncNormalizePreprocessor();

        // Heavyweight 门控先于 SemanticModel/dirty 检查。
        Assert.Throws<NotSupportedException>(() =>
            preprocessor.Preprocess("class C {}", null, new AsyncNormalizeOptions
            {
                Mode = AsyncNormalizeMode.Heavyweight,
            }));
    }

    [Fact]
    public void Options_With_KeepsOtherValues()
    {
        var options = new AsyncNormalizeOptions { Strict = false };

        Assert.False(options.Strict);
        Assert.Equal(AsyncNormalizeMode.Lightweight, options.Mode);
        Assert.True(options.StripConfigureAwait);
    }

    // ── 2. Statistics ──

    [Fact]
    public void Statistics_NewInstance_AllZero()
    {
        var stats = new AsyncNormalizeStatistics();

        Assert.Equal(0, stats.FilesScanned);
        Assert.Equal(0, stats.FilesTransformed);
        Assert.Equal(0, stats.FilesSkippedClean);
        Assert.Equal(0, stats.FilesDirtyNoRewrite);
        Assert.Equal(0, stats.ResidualCount);
        Assert.Equal(0, stats.AsyncMethodsSeen);
        Assert.Equal(0, stats.AwaitSites);
        Assert.Equal(0, stats.ConfigureAwaitStripped);
        Assert.Equal(0, stats.AsyncVoidRejected);
        Assert.Equal(0, stats.UnsupportedAwaiters);
        Assert.Equal(0, stats.SkippedUnsupported);
    }

    // ── 3. Diagnostic ──

    [Fact]
    public void Diagnostic_Fields_With_ToMsBuildString()
    {
        var diagnostic = new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1801,
            "unsupported awaitable",
            "File.cs",
            7);

        Assert.Equal(AsyncNormalizeSeverity.Error, diagnostic.Severity);
        Assert.Equal(AsyncNormalizeDiagnosticCode.CS2J1801, diagnostic.Code);
        Assert.Equal("unsupported awaitable", diagnostic.Message);
        Assert.Equal("File.cs", diagnostic.FilePath);
        Assert.Equal(7, diagnostic.Line);

        // 枚举成员名即码值。
        Assert.Equal("CS2J1801", diagnostic.Code.ToString());

        var msBuild = diagnostic.ToMsBuildString();
        Assert.Contains("CS2J1801", msBuild);
        Assert.Contains("unsupported awaitable", msBuild);
        Assert.Contains("error", msBuild);
        Assert.Contains("File.cs(7)", msBuild);

        var downgraded = diagnostic with { Severity = AsyncNormalizeSeverity.Warning };
        Assert.Equal(AsyncNormalizeSeverity.Warning, downgraded.Severity);
        Assert.Equal(AsyncNormalizeDiagnosticCode.CS2J1801, downgraded.Code);
        Assert.Contains("warning", downgraded.ToMsBuildString());

        var info = new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Info,
            AsyncNormalizeDiagnosticCode.CS2J1830,
            "stripped");
        Assert.DoesNotContain("(", info.ToMsBuildString());
        Assert.Contains("info CS2J1830", info.ToMsBuildString());
    }

    // ── 4. Naming ──

    [Fact]
    public void Naming_LiftedMethodSequence_And_WrapperSuffix()
    {
        Assert.Equal("Cs2jAsync_RunAsync_0", AsyncNormalizeNaming.GetLiftedMethodName("RunAsync", 0));
        Assert.Equal("Cs2jAsync_RunAsync_1", AsyncNormalizeNaming.GetLiftedMethodName("RunAsync", 1));
        Assert.Equal("Cs2jAsync_Refresh_0_w",
            AsyncNormalizeNaming.GetWrapperMethodName("Cs2jAsync_Refresh_0"));
    }

    [Fact]
    public void Naming_Fields_And_Temp()
    {
        Assert.Equal("_cs2jThis", AsyncNormalizeNaming.ThisField);
        Assert.Equal("_cs2jCap_x", AsyncNormalizeNaming.GetCaptureFieldName("x"));
        Assert.Equal("_cs2jLoc_y", AsyncNormalizeNaming.GetLocalFieldName("y"));
        Assert.Equal("_cs2jTmp_0", AsyncNormalizeNaming.GetTempName(0));
        Assert.Equal("_cs2jTmp_3", AsyncNormalizeNaming.GetTempName(3));
    }

    [Fact]
    public void Naming_ResolveUniqueName_AppendsNumericSuffix()
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "Foo" };

        var first = AsyncNormalizeNaming.ResolveUniqueName("Foo", used);
        Assert.Equal("Foo2", first);
        Assert.Contains("Foo2", used);

        var second = AsyncNormalizeNaming.ResolveUniqueName("Foo", used);
        Assert.Equal("Foo3", second);

        var free = AsyncNormalizeNaming.ResolveUniqueName("Bar", used);
        Assert.Equal("Bar", free);
    }

    // ── 5. ConfigureAwait(bool) 剥离 ──

    [Fact]
    public void Strip_ConfigureAwait_False_PreservesAwait()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        await x.ConfigureAwait(false);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("await x", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Info
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1830);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Strip_ConfigureAwait_True_PreservesAwait()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        await x.ConfigureAwait(true);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("await x", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Strip_ConfigureAwait_NonConstantBoolArgument()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        bool continueOnCapturedContext = false;
        await x.ConfigureAwait(continueOnCapturedContext);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("continueOnCapturedContext", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Strip_ChainedCalls_UnrelatedCallStays_InnerConfigureAwaitStrips()
    {
        const string src = @"
using System.Threading.Tasks;
class Foo {
    public Task<int> GetAwait(int n) => Task.FromResult(n);
}
class C {
    public async Task M() {
        var f = new Foo();
        await f.GetAwait(1).ConfigureAwait(false);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("GetAwait(1)", result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("await f.GetAwait(1)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Strip_MultipleConfigureAwaitSites_StripsAll()
    {
        // 注：x.ConfigureAwait(false).ConfigureAwait(false) 在 C# 中不能编译
        // （ConfiguredTaskAwaitable 没有第二个 ConfigureAwait），故用两个连续站点
        // 验证多点剥离计数；中间为 Task 返回值的链式调用由 GetAwait 用例覆盖。
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        await x.ConfigureAwait(false);
        await x.ConfigureAwait(true);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Equal(2, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 6. 自定义同名扩展不得误伤 ──

    [Fact]
    public void Strip_CustomSameNameExtension_OnNonTaskReceiver_NotStripped()
    {
        const string src = @"
using System.Threading.Tasks;
class Foo {
    public Task<int> GetAwait(int n) => Task.FromResult(n);
}
static class FooExtensions {
    public static Task ConfigureAwait(this Foo foo, bool continueOnCapturedContext)
        => Task.CompletedTask;
}
class C {
    public async Task M() {
        var f = new Foo();
        await f.GetAwait(1);
        await f.ConfigureAwait(false);
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains("ConfigureAwait", result.OutputCode);
        Assert.Equal(0, result.Statistics.ConfigureAwaitStripped);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1830);
        // 非 Task receiver 的同名扩展不计 residual 违规。
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1810);
    }

    // ── 7. ConfigureAwaitOptions / 强类型 Configured*Awaitable ──

    [Fact]
    public void ConfigureAwaitOptions_Strict_Error_Unchanged()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M() {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    [Fact]
    public void ConfigureAwaitOptions_NonStrict_Warning()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M() {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}";
        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1808);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == AsyncNormalizeSeverity.Error);
    }

    [Fact]
    public void ConfiguredTaskAwaitable_StrongTypedDeclarations_Error()
    {
        const string src = @"
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
class C {
    private readonly ConfiguredTaskAwaitable _field;
    public C(ConfiguredTaskAwaitable parameter) {
        _field = parameter;
    }
    public async Task M() {
        ConfiguredTaskAwaitable local = default;
        await Task.CompletedTask;
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        var strongTyped = result.Diagnostics
            .Where(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1808)
            .ToList();
        Assert.Equal(3, strongTyped.Count);
        Assert.All(strongTyped, d => Assert.Equal(AsyncNormalizeSeverity.Error, d.Severity));
    }

    // ── 8. async void ──

    [Fact]
    public void AsyncVoid_Method_And_LocalFunction_Strict_Error_Verbatim()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async void M() {
        async void Local() {
        }
        await Task.CompletedTask;
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        var voidDiagnostics = result.Diagnostics
            .Where(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1803)
            .ToList();
        Assert.Equal(2, voidDiagnostics.Count);
        Assert.All(voidDiagnostics, d => Assert.Equal(AsyncNormalizeSeverity.Error, d.Severity));
        Assert.Equal(2, result.Statistics.AsyncVoidRejected);
    }

    [Fact]
    public void AsyncVoid_NonStrict_Warning_NoError()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async void M() {
        await Task.CompletedTask;
    }
}";
        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1803);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == AsyncNormalizeSeverity.Error);
    }

    // ── 9. dynamic / 自定义 awaitable / 正常 Task ──

    [Fact]
    public void AwaitDynamic_Strict_Error()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(dynamic d) {
        await d;
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1801);
        Assert.Equal(1, result.Statistics.UnsupportedAwaiters);
    }

    [Fact]
    public void AwaitCustomAwaitable_Strict_Error()
    {
        const string src = @"
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
class MyAwaitable {
    public MyAwaiter GetAwaiter() => new MyAwaiter();
}
struct MyAwaiter : INotifyCompletion {
    public bool IsCompleted => true;
    public void OnCompleted(Action continuation) {
    }
    public void GetResult() {
    }
}
class C {
    public async Task M(MyAwaitable a) {
        await a;
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1801);
        Assert.Equal(1, result.Statistics.UnsupportedAwaiters);
    }

    [Fact]
    public void AwaitNormalTaskDelay_No1801_AwaitPreserved()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M() {
        await Task.Delay(1);
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1801);
        Assert.Contains("await Task.Delay(1)", result.OutputCode);
        Assert.Equal(1, result.Statistics.AwaitSites);
        Assert.Equal(1, result.Statistics.AsyncMethodsSeen);
    }

    // ── 10. lock 内 await：基线 CS1996 优先，不重复发 CS2J1805 ──

    [Fact]
    public void LockAwait_BaselineAlreadyReportsCS1996_NoDefensive1805()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(object gate, Task t) {
        lock (gate) {
            await t;
        }
    }
}";
        var model = ParseWithSemantics(src);

        // 前置：基线快照确实能看到 CS1996。
        Assert.Contains(model.Compilation.GetDiagnostics(), d => d.Id == "CS1996");

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1805);
    }

    // ── 11. 干净文件 ──

    [Fact]
    public void CleanFile_NoAsyncConstructs_SkippedClean()
    {
        const string src = @"
class C {
    public int M() => 42;
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void NullSemanticModel_SkippedClean()
    {
        var result = new AsyncNormalizePreprocessor()
            .Preprocess("class C {}", null, new AsyncNormalizeOptions());

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    // ── 12. 幂等 ──

    [Fact]
    public void Strip_Idempotent_SecondRunUnchanged()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        await x.ConfigureAwait(false);
    }
}";
        var first = Run(src);
        Assert.True(first.Changed);
        Assert.DoesNotContain("ConfigureAwait", first.OutputCode);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(0, second.Statistics.ConfigureAwaitStripped);
        Assert.DoesNotContain("ConfigureAwait", second.OutputCode);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    // ── 统计桶综合 ──

    [Fact]
    public void Statistics_AggregateAcrossMixedSites()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task x) {
        await Task.Delay(1);
        await x.ConfigureAwait(false);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.FilesScanned);
        Assert.Equal(1, result.Statistics.AsyncMethodsSeen);
        Assert.Equal(2, result.Statistics.AwaitSites);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.AsyncVoidRejected);
        Assert.Equal(0, result.Statistics.UnsupportedAwaiters);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 13. unsafe 上下文 await（CS2J1809 基线优先）；ref struct/ref local 跨 await
    //       在 C# 13+ 已合法（Roslyn 5 不发 CS4007/CS9217），转换器不得制造假诊断。 ──

    [Fact]
    public void UnsafeAwait_BaselineReportsCS4004_NoDefensive1809()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task t) {
        unsafe {
            await t;
        }
    }
}";
        var model = ParseWithSemantics(src);

        Assert.Contains(model.Compilation.GetDiagnostics(), d => d.Id == "CS4004");

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1809);
    }

    [Fact]
    public void RefStructAcrossAwait_LegalSinceCSharp13_ZeroDiagnostics()
    {
        // 探针证据：Roslyn 5 对 CSharp13..Latest 不发 CS4007（旧版本由声明面
        // CS8936/CS9058/CS9202 拦截）。转换器不增设 CS2J1806 镜像。
        const string src = @"
using System;
using System.Threading.Tasks;
class C {
    public async Task M(Task t) {
        Span<int> s = stackalloc int[4];
        await t;
        s[0] = 1;
    }
}";
        var model = ParseWithSemantics(src);

        Assert.DoesNotContain(model.Compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.Equal(0, result.Diagnostics.Count);
    }

    [Fact]
    public void RefLocalAcrossAwait_LegalSinceCSharp13_ZeroDiagnostics()
    {
        const string src = @"
using System.Threading.Tasks;
class C {
    public async Task M(Task t) {
        int x = 1;
        ref int r = ref x;
        await t;
        r = 2;
    }
}";
        var model = ParseWithSemantics(src);

        Assert.DoesNotContain(model.Compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.Equal(0, result.Diagnostics.Count);
    }

    // ── Helpers ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static AsyncNormalizeFileResult Run(string src, AsyncNormalizeOptions? options = null)
    {
        var model = ParseWithSemantics(src);
        return new AsyncNormalizePreprocessor().Preprocess(src, model, options ?? new AsyncNormalizeOptions());
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

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-normalize-test",
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
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
