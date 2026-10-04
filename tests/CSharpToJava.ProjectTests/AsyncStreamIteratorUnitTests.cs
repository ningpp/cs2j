using System.Reflection;
using System.Text.RegularExpressions;
using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C38 M4：async 迭代器方法联合状态机降级（非 async 工厂 + Cs2jAsyncIterator_*
/// 枚举器类 + Cs2jAsyncEnumerable_* 包装类；设计 D7、§3.2 AS-6、§4.4）。夹具同
/// AsyncStreamAwaitForeachUnitTests（Latest 语法 + TPA references）。正例额外要求
/// 重写产物 compilation.GetDiagnostics() 完全为空（含 warning），并用 Assembly.Load
/// 真实执行生成的枚举器，锁定期望产出序列（联合状态机的运行时语义 oracle）。
/// </summary>
public class AsyncStreamIteratorUnitTests
{
    private const string Preamble = @"
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

class HandmadeEnumerable : IAsyncEnumerable<int>
{
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new HandmadeEnumerator();
}

class HandmadeEnumerator : IAsyncEnumerator<int>
{
    private int _current;
    public int Current => _current;
    public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(++_current <= 3);
    public ValueTask DisposeAsync() => default;
}

class MyRes : IDisposable
{
    public static int DisposeCount;
    public void Dispose() => DisposeCount++;
}

class MyAsyncRes : IAsyncDisposable
{
    public static int DisposeCount;
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return default;
    }
}

class C
{
    public int Seed = 100;
";

    private const string Suffix = @"
}
";

    private static string InClass(string body) => Preamble + body + Suffix;

    // ── 组 1：基本多 yield（包装类 + 枚举器类），真实 await 保留，运行时序列一致 ──

    [Fact]
    public async Task Group01_BasicMultipleYields_WrapperAndEnumerator_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        await Task.Delay(1);
        yield return 1;
        yield return 2;
        yield return 3;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("Cs2jAsyncIterator_C_0", compact);
        Assert.Contains("Cs2jAsyncEnumerable_C_0", compact);
        Assert.Contains("MoveNextAsync", compact);
        Assert.Contains("GetAsyncEnumerator", compact);
        Assert.Contains("publicasyncglobal::System.Threading.Tasks.ValueTask<bool>MoveNextAsync()", compact);
        Assert.DoesNotContain("asyncIAsyncEnumerable", compact.Replace(" ", ""));
        Assert.Equal(1, result.Statistics.AsyncIteratorsLowered);
        Assert.Equal(0, result.Statistics.AsyncIteratorsRejected);
        Assert.Equal(2, result.Statistics.AsyncIteratorClassesEmitted);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var sequence = await EnumerateRuntime(result.OutputCode, "Mk");
        Assert.Equal(new[] { 1, 2, 3 }, sequence);
    }

    // ── 组 2：await 与 yield 交错（异步挂起 + yield 跨调用状态共存） ──

    [Fact]
    public async Task Group02_AwaitAndYieldInterleaved_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        yield return 1;
        await Task.Delay(1);
        yield return 2;
        await Task.Delay(1);
        yield return 3;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.AsyncIteratorsLowered);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var sequence = await EnumerateRuntime(result.OutputCode, "Mk");
        Assert.Equal(new[] { 1, 2, 3 }, sequence);
    }

    // ── 组 3：while 含 yield（线性化 cond/continue/end 标签） ──

    [Fact]
    public async Task Group03_WhileWithYield_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        int i = 0;
        while (i < 3)
        {
            await Task.Delay(1);
            yield return i;
            i++;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 0, 1, 2 }, await EnumerateRuntime(result.OutputCode, "Mk"));
    }

    // ── 组 4：for 含 yield + do-while 含 yield（无 break 形态标签剪枝） ──

    [Fact]
    public async Task Group04_ForAndDoWithYield_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> For_()
    {
        for (int i = 0; i < 2; i++)
        {
            yield return i;
        }
    }

    public async IAsyncEnumerable<int> Do_()
    {
        int j = 0;
        do
        {
            yield return j;
            j++;
        } while (j < 2);
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.AsyncIteratorsLowered);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 0, 1 }, await EnumerateRuntime(result.OutputCode, "For_"));
        Assert.Equal(new[] { 0, 1 }, await EnumerateRuntime(result.OutputCode, "Do_"));
    }

    // ── 组 5：同步 foreach 区域（GetEnumerator/MoveNext/Current + finally Dispose） ──

    [Fact]
    public async Task Group05_SyncForeachRegion_FieldDriven_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        foreach (int x in new[] { 4, 5, 6 })
        {
            await Task.Delay(1);
            yield return x;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("GetEnumerator()", compact);
        Assert.Contains("MoveNext()", compact);
        Assert.Contains(".Current", compact);
        Assert.Contains("((global::System.IDisposable)", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 4, 5, 6 }, await EnumerateRuntime(result.OutputCode, "Mk"));
    }

    // ── 组 6：try-finally + yield 区域：正常跑完 finally 执行；挂起中 DisposeAsync 也释放 ──

    [Fact]
    public async Task Group06_TryFinallyWithYield_NormalAndSuspendedDispose_BothReleaseOnce()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        using (var r = new MyRes())
        {
            try
            {
                yield return 1;
                yield return 2;
            }
            finally
            {
                Console.WriteLine(""finally"");
            }
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.AsyncIteratorsLowered);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var asm = LoadOutputAssembly(result.OutputCode);
        ResetCounter(asm, "MyRes", "DisposeCount");
        Assert.Equal(new[] { 1, 2 }, await EnumerateRuntime(asm, "Mk"));
        Assert.Equal(1, GetCounter(asm, "MyRes", "DisposeCount"));

        // 挂起在第一个 yield 时 DisposeAsync：沿区域链补放 finally/using（不重入）。
        ResetCounter(asm, "MyRes", "DisposeCount");
        var suspended = await StartFirstAndSuspend(asm, "Mk");
        await suspended.DisposeAsync();
        Assert.Equal(1, GetCounter(asm, "MyRes", "DisposeCount"));
        // 释放后再 MoveNext 终态 false（state==-1 短路）。
        Assert.False(await suspended.MoveNextAsync());
    }

    // ── 组 7：嵌套 try-finally（链式重入，反向释放顺序） ──

    [Fact]
    public async Task Group07_NestedTryFinally_ChainReentry_LifoRelease_RuntimeSequence()
    {
        var src = InClass(@"
    public static List<string> Trace = new List<string>();

    public async IAsyncEnumerable<int> Mk()
    {
        try
        {
            Trace.Add(""outer-enter"");
            yield return 1;
            try
            {
                Trace.Add(""inner-enter"");
                yield return 2;
            }
            finally
            {
                Trace.Add(""inner-finally"");
            }
            yield return 3;
        }
        finally
        {
            Trace.Add(""outer-finally"");
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        // 两个 run flag（区域 0/1 各一个字段）+ 两个 ReEntryLabel + 链式边界 switch。
        var compact = Compact(result.OutputCode);
        var runFlagFields = Regex.Matches(compact, "__cs2jAsItRun_0_\\d+")
            .Select(m => m.Value)
            .Distinct()
            .Count();
        Assert.Equal(2, runFlagFields);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var asm = LoadOutputAssembly(result.OutputCode);
        var trace = (List<string>)asm.GetType("C")!.GetField("Trace",
            BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        trace.Clear();
        Assert.Equal(new[] { 1, 2, 3 }, await EnumerateRuntime(asm, "Mk"));
        Assert.Equal(new[]
        {
            "outer-enter", "inner-enter", "inner-finally", "outer-finally",
        }, trace);
    }

    // ── 组 8：yield break（goto end；try-finally 沿途展开） ──

    [Fact]
    public async Task Group08_YieldBreak_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk(int stopAt)
    {
        for (int i = 0; i < 10; i++)
        {
            if (i == stopAt)
                yield break;
            yield return i;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 0, 1 }, await EnumerateRuntime(result.OutputCode, "Mk", 2));
    }

    // ── 组 9：直返 IAsyncEnumerator<T>：无包装类，工厂直接 new 枚举器 ──

    [Fact]
    public async Task Group09_IAsyncEnumeratorDirectReturn_NoWrapperClass()
    {
        var src = InClass(@"
    public async IAsyncEnumerator<int> Mk()
    {
        yield return 7;
        await Task.Delay(1);
        yield return 8;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("Cs2jAsyncIterator_C_0", compact);
        Assert.DoesNotContain("Cs2jAsyncEnumerable_C_0", compact);
        Assert.Equal(1, result.Statistics.AsyncIteratorsLowered);
        Assert.Equal(1, result.Statistics.AsyncIteratorClassesEmitted);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var asm = LoadOutputAssembly(result.OutputCode);
        var instance = Activator.CreateInstance(asm.GetType("C")!, true);
        var enumerator = (IAsyncEnumerator<int>)asm.GetType("C")!
            .GetMethod("Mk", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, null)!;
        var values = new List<int>();
        try
        {
            while (await enumerator.MoveNextAsync())
                values.Add(enumerator.Current);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        Assert.Equal(new[] { 7, 8 }, values);
    }

    // ── 组 10：泛型方法（类型参数/约束复制到嵌套类，new 带 TypeArgumentList） ──

    [Fact]
    public async Task Group10_GenericMethod_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<T> Mk<T>(T first, T second) where T : class
    {
        await Task.Delay(1);
        yield return first;
        yield return second;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("Cs2jAsyncIterator_C_0<T>", compact);
        Assert.Contains("Cs2jAsyncEnumerable_C_0<T>", compact);
        Assert.Contains("whereT:class", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { "a", "b" }, await EnumerateRuntime<string>(result.OutputCode, "Mk", "a", "b"));
    }

    // ── 组 11：static 工厂（无 this 字段） ──

    [Fact]
    public async Task Group11_StaticMethod_NoThisField()
    {
        var src = InClass(@"
    public static async IAsyncEnumerable<int> Mk()
    {
        yield return 9;
        yield return 10;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.DoesNotContain("__cs2jAsItThis_0", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 9, 10 }, await EnumerateRuntime(result.OutputCode, "Mk"));
    }

    // ── 组 12：实例 this 捕获（字段访问经 __cs2jAsItThis_n） ──

    [Fact]
    public async Task Group12_InstanceThisCapture_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        yield return Seed;
        await Task.Delay(1);
        yield return Seed + 1;
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("__cs2jAsItThis_0", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 100, 101 }, await EnumerateRuntime(result.OutputCode, "Mk"));
    }

    // ── 组 13：CancellationToken 形参 + [EnumeratorCancellation] 合并语义 ──

    [Fact]
    public async Task Group13_EnumeratorCancellation_MergesConsumerToken()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(1000, cancellationToken);
            yield return i;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("CanBeCanceled", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var asm = LoadOutputAssembly(result.OutputCode);
        var instance = Activator.CreateInstance(asm.GetType("C")!, true);
        var enumerable = (IAsyncEnumerable<int>)asm.GetType("C")!
            .GetMethod("Mk", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, new object[] { CancellationToken.None })!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enumerator = enumerable.GetAsyncEnumerator(cts.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ── 组 14：switch（常量 case 线性化）+ if 分支中的 yield ──

    [Fact]
    public async Task Group14_SwitchConstLabelsAndIf_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk(int mode)
    {
        switch (mode)
        {
            case 1:
                yield return 10;
                break;
            case 2:
                yield return 20;
                yield return 21;
                break;
            default:
                yield return 99;
                break;
        }
        if (mode == 1)
        {
            yield return 11;
        }
        else
        {
            yield return 0;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 10, 11 }, await EnumerateRuntime(result.OutputCode, "Mk", 1));
        Assert.Equal(new[] { 20, 21, 0 }, await EnumerateRuntime(result.OutputCode, "Mk", 2));
        Assert.Equal(new[] { 99, 0 }, await EnumerateRuntime(result.OutputCode, "Mk", 7));
    }

    // ── 组 15：同步 using 语句区域（资源字段 + finally Dispose） ──

    [Fact]
    public async Task Group15_SyncUsingRegion_NullGuardAndDispose_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        using (var r = new MyRes())
        {
            yield return 1;
            await Task.Delay(1);
            yield return 2;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.AsyncIteratorRegionsEmitted);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);

        var asm = LoadOutputAssembly(result.OutputCode);
        ResetCounter(asm, "MyRes", "DisposeCount");
        Assert.Equal(new[] { 1, 2 }, await EnumerateRuntime(asm, "Mk"));
        Assert.Equal(1, GetCounter(asm, "MyRes", "DisposeCount"));

        ResetCounter(asm, "MyRes", "DisposeCount");
        var suspended = await StartFirstAndSuspend(asm, "Mk");
        await suspended.DisposeAsync();
        Assert.Equal(1, GetCounter(asm, "MyRes", "DisposeCount"));
    }

    // ── 组 16：幂等 / 确定性 / 防撞 / 无 <> 编译器名 ──

    [Fact]
    public void Group16_Idempotent_Deterministic_Collision_NoCompilerGeneratedNames()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        int __cs2jAsItState_0 = 1;
        yield return __cs2jAsItState_0;
        yield return 2;
    }
");

        var first = Run(src);
        Assert.True(first.Changed);
        Assert.DoesNotContain(first.OutputCode, "<>");
        Assert.Contains("Cs2jAsyncIterator_C_0", first.OutputCode);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);

        var deterministic = Run(src);
        Assert.Equal(first.OutputCode, deterministic.OutputCode);
    }

    // ── 组 17：生产者体内 await foreach 先展开、再联合降级（端到端联合状态机） ──

    [Fact]
    public async Task Group17_AwaitForeachInsideProducer_JointLowering_RuntimeSequence()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        await foreach (var z in new HandmadeEnumerable())
        {
            await Task.Delay(1);
            yield return z * 10;
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.AsyncIteratorsLowered);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        var compact = Compact(result.OutputCode);
        Assert.DoesNotContain("awaitforeach", compact);
        Assert.DoesNotContain("yieldreturn", compact);
        AssertOutputCompilesZeroDiagnostics(result.OutputCode);
        Assert.Equal(new[] { 10, 20, 30 }, await EnumerateRuntime(result.OutputCode, "Mk"));
    }

    // ── 组 18：局部函数 → CS2J1844，严格整体不改写 ──

    [Fact]
    public void Group18_LocalFunction_Strict_1844NoRewrite()
    {
        var src = InClass(@"
    public async Task M()
    {
        async IAsyncEnumerable<int> Local()
        {
            yield return 1;
            yield break;
        }

        await foreach (var x in Local())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844
            && d.Message.Contains("local function"));
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
        Assert.Equal(0, result.Statistics.AsyncIteratorsLowered);
    }

    // ── 组 19：kill-switch 两态（显式拒绝 / 能力子开关关闭）均 1844 ──

    [Fact]
    public void Group19_RejectAsyncIteratorKillSwitch_Strict_1844()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        yield return 1;
    }
");

        var rejected = Run(src, new AsyncNormalizeOptions { RejectAsyncIterator = true });
        Assert.False(rejected.Changed);
        Assert.Contains(rejected.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.Equal(0, rejected.Statistics.AsyncIteratorsLowered);

        var capabilityOff = Run(src, new AsyncNormalizeOptions { EnableAsyncIteratorLowering = false });
        Assert.False(capabilityOff.Changed);
        Assert.Contains(capabilityOff.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.Equal(0, capabilityOff.Statistics.AsyncIteratorsLowered);
    }

    // ── 组 20：v1 不支持形态（yield in try-catch）→ 1844 ──

    [Fact]
    public void Group20_YieldInsideTryCatch_Strict_1844()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        try
        {
            yield return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
");

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844
            && d.Message.Contains("try/catch"));
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
    }

    // ── 组 21：非严格 1844 降 Warning（局部函数形态保持冻结） ──

    [Fact]
    public void Group21_LocalFunction_NonStrict_Warning()
    {
        var src = InClass(@"
    public async Task M()
    {
        async IAsyncEnumerable<int> Local()
        {
            yield return 1;
        }

        await foreach (var x in Local())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed); // 消费侧 await foreach 仍展开
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.Equal(0, result.Statistics.AsyncIteratorsLowered);
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
    }

    // ── Helpers（夹具同 AsyncStreamAwaitForeachUnitTests） ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(BuildReferences);

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

    /// <summary>正例闸门：重写产物零编译诊断（Error 与 Warning 全空，含 CS1998/CS0164）。</summary>
    private static void AssertOutputCompilesZeroDiagnostics(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-iterator-probe-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
        Assert.True(diagnostics.Count == 0, "rewritten output has compile diagnostics: " +
            string.Join("; ", diagnostics.Select(e => e.ToString())));
    }

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-iterator-test-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var result = new List<MetadataReference>();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    result.Add(MetadataReference.CreateFromFile(assembly));
            }
        }
        else
        {
            result.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return result;
    }

    /// <summary>把重写产物编译为内存 DLL 并加载（程序集名唯一，避免重复加载缓存）。</summary>
    private static Assembly LoadOutputAssembly(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-iterator-asm-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, "emit failed: " +
            string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        return Assembly.Load(pe.ToArray());
    }

    private static int GetCounter(Assembly asm, string typeName, string fieldName)
        => (int)asm.GetType(typeName)!
            .GetField(fieldName, BindingFlags.Public | BindingFlags.Static
                | BindingFlags.NonPublic)!
            .GetValue(null)!;

    private static void ResetCounter(Assembly asm, string typeName, string fieldName)
        => asm.GetType(typeName)!
            .GetField(fieldName, BindingFlags.Public | BindingFlags.Static
                | BindingFlags.NonPublic)!
            .SetValue(null, 0);

    /// <summary>反射调用 C.Mk(...)，经接口真实 await 枚举，返回产出序列。</summary>
    private static Task<List<int>> EnumerateRuntime(
        string code, string methodName, params object[] arguments)
        => EnumerateRuntime<int>(code, methodName, arguments);

    private static Task<List<int>> EnumerateRuntime(
        Assembly asm, string methodName, params object[] arguments)
        => EnumerateRuntime<int>(asm, methodName, arguments);

    private static async Task<List<T>> EnumerateRuntime<T>(
        string code, string methodName, params object[] arguments)
        => await EnumerateRuntime<T>(LoadOutputAssembly(code), methodName, arguments);

    private static async Task<List<T>> EnumerateRuntime<T>(
        Assembly asm, string methodName, params object[] arguments)
    {
        var type = asm.GetType("C")!;
        var instance = type.IsAbstract && type.IsSealed
            ? null
            : Activator.CreateInstance(type, true);
        var method = type.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!;
        // 泛型生产者（如 Mk<T>）：用元素类型实参闭合后再反射调用。
        if (method.IsGenericMethodDefinition)
            method = method.MakeGenericMethod(typeof(T));
        var produced = method.Invoke(instance, arguments)!;

        if (produced is IAsyncEnumerable<T> enumerable)
        {
            var enumerator = enumerable.GetAsyncEnumerator();
            try
            {
                return await Drain(enumerator);
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        return await Drain((IAsyncEnumerator<T>)produced);
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerator<T> enumerator)
    {
        var values = new List<T>();
        while (await enumerator.MoveNextAsync())
            values.Add(enumerator.Current);
        return values;
    }

    /// <summary>取到第一个元素后停住（挂起态），返回仍打开的枚举器供 DisposeAsync 探测。</summary>
    private static Task<IAsyncEnumerator<int>> StartFirstAndSuspend(
        Assembly asm, string methodName, params object[] arguments)
        => StartFirstAndSuspend<int>(asm, methodName, arguments);

    private static async Task<IAsyncEnumerator<T>> StartFirstAndSuspend<T>(
        Assembly asm, string methodName, params object[] arguments)
    {
        var type = asm.GetType("C")!;
        var instance = Activator.CreateInstance(type, true);
        var enumerable = (IAsyncEnumerable<T>)type
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)!
            .Invoke(instance, arguments)!;
        var enumerator = enumerable.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, (int)(object)enumerator.Current!);
        return enumerator;
    }

    private static string Compact(string code)
        => Regex.Replace(code, @"\s+", string.Empty);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}

