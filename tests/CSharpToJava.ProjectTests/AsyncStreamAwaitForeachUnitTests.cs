using System.Text.RegularExpressions;
using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C38 M3/3b：<c>await foreach</c> → 显式 IAsyncEnumerator 驱动（AS-1/AS-2/AS-3）、
/// WithCancellation/ConfigureAwait 链消解、AS-6 异步迭代器冻结（CS2J1844）、
/// span 漂移回归（任务6）。夹具同 AsyncStreamAwaitUsingUnitTests：Latest 语法 +
/// TPA references；消费侧枚举源全部手写（非 async 修饰，避免生产者被 AS-6 冻结）。
/// </summary>
public class AsyncStreamAwaitForeachUnitTests
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
    public static IAsyncEnumerable<int> GetSource() => new HandmadeEnumerable();
}

class HandmadeEnumerator : IAsyncEnumerator<int>
{
    private int _current;
    public int Current => _current;
    public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(++_current <= 3);
    public ValueTask DisposeAsync() => default;
}

class DuckEnumerator
{
    public int Current => 0;
    public ValueTask<bool> MoveNextAsync() => new ValueTask<bool>(false);
    public ValueTask DisposeAsync() => default;
}

// 模式型（鸭子）异步枚举源：编译器接受 pattern-based async enumeration，但 v1 只接接口（CS2J1840）。
class DuckEnumerable
{
    public DuckEnumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new DuckEnumerator();
}

// 自定义非 BCL 取消链方法：实例方法优先于扩展方法绑定（CS2J1846 非 BCL/非 CT）。
class CustomEnumerable : IAsyncEnumerable<int>
{
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new HandmadeEnumerator();
    public CustomEnumerable WithCancellation(int ticks) => this;
}

class MyRes : IAsyncDisposable
{
    public ValueTask DisposeAsync() => default;
}

class C
{
";

    private const string Suffix = @"
}
";

    private static string InClass(string body) => Preamble + body + Suffix;

    // ── 组 1：基本四件套（GetAsyncEnumerator/MoveNextAsync/Current/DisposeAsync）+
    //         await 守恒：循环体原有 await 不丢不重；默认朴素 try/finally 机械面
    //         新增 2 个 await（while 的 MoveNextAsync + finally 的 DisposeAsync） ──

    [Fact]
    public void Group01_BasicExpansion_FourPieceSet_AndBodyAwaitsConserved()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            await Task.Delay(1);
            Console.WriteLine(x);
            await Task.Delay(2);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("GetAsyncEnumerator(global::System.Threading.CancellationToken.None)", compact);
        Assert.Contains("MoveNextAsync()", compact);
        Assert.Contains("__cs2jAsE_0.Current", compact);
        Assert.Contains("DisposeAsync()", compact);
        // 默认朴素 try/finally：无保真标志位、单层 finally。
        Assert.DoesNotContain("__cs2jAsOk_", compact);
        Assert.Contains("finally{await__cs2jAsE_0.DisposeAsync();}", compact);
        // 循环体两个 await 原样各保留一次。
        Assert.Equal(1, CountOccurrences(compact, "awaitTask.Delay(1);"));
        Assert.Equal(1, CountOccurrences(compact, "awaitTask.Delay(2);"));
        // 朴素形态机械 await：while(MoveNextAsync) + finally Dispose = 2，
        // 叠加循环体 2 个，共 4。
        Assert.Equal(4, CountOccurrences(compact, "await"));
        Assert.DoesNotContain("awaitforeach", compact);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(1, result.Statistics.AsyncEnumeratorsEmitted);
        Assert.Equal(0, result.Statistics.WithCancellationResolved);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 2：取消链——裸源显式 None；WithCancellation(ct) 实参进 GetAsyncEnumerator ──

    [Fact]
    public void Group02_BareSource_PassesExplicitCancellationTokenNone()
    {
        var src = InClass(@"
    public async Task M()
    {
        var src = new HandmadeEnumerable();
        await foreach (var x in src)
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("src.GetAsyncEnumerator(global::System.Threading.CancellationToken.None)", compact);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group02_WithCancellation_ForwardedToGetAsyncEnumerator_AndChainGone()
    {
        var src = InClass(@"
    public async Task M(CancellationToken ct)
    {
        var src = new HandmadeEnumerable();
        await foreach (var x in src.WithCancellation(ct))
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("src.GetAsyncEnumerator(ct)", compact);
        // 调用点的链调用已消失（夹具里自定义同名方法声明不算调用点）。
        Assert.DoesNotContain(".WithCancellation", compact);
        Assert.Equal(1, result.Statistics.WithCancellationResolved);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 3：ConfigureAwait(false/true/非常量 bool) 三形态剥离 + 链序 +
    //         强类型 ConfiguredCancelableAsyncEnumerable<T> → CS2J1843 ──

    [Fact]
    public void Group03_ConfigureAwait_FalseTrue_Variable_AllStripped_Info1830()
    {
        var src = InClass(@"
    public async Task M(bool flag)
    {
        var src = new HandmadeEnumerable();
        await foreach (var a in src.ConfigureAwait(false)) Console.WriteLine(a);
        await foreach (var b in src.ConfigureAwait(true)) Console.WriteLine(b);
        await foreach (var c in src.ConfigureAwait(flag)) Console.WriteLine(c);
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", compact);
        Assert.Equal(3, CountOccurrences(compact, "src.GetAsyncEnumerator("));
        Assert.Equal(3, result.Statistics.EnumerableConfigureAwaitStripped);
        Assert.Equal(3, result.Statistics.AwaitForeachExpanded);
        Assert.Contains(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1830);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group03_ChainOrder_WithCancellationThenConfigureAwait_ResolvedBoth()
    {
        var src = InClass(@"
    public async Task M(CancellationToken ct)
    {
        var src = new HandmadeEnumerable();
        await foreach (var x in src.WithCancellation(ct).ConfigureAwait(false))
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("src.GetAsyncEnumerator(ct)", compact);
        Assert.DoesNotContain(".WithCancellation", compact);
        Assert.DoesNotContain(".ConfigureAwait", compact);
        Assert.Equal(1, result.Statistics.WithCancellationResolved);
        Assert.Equal(1, result.Statistics.EnumerableConfigureAwaitStripped);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group03_StronglyTypedConfiguredCancelable_1843_SingleDiagnostic()
    {
        var src = InClass(@"
    public async Task M(CancellationToken ct)
    {
        var src = new HandmadeEnumerable();
        ConfiguredCancelableAsyncEnumerable<int> cfg = src.WithCancellation(ct);
        await foreach (var x in cfg)
        {
            Console.WriteLine(x);
        }
    }
");

        var strict = Run(src);
        Assert.False(strict.Changed);
        var strict1843 = strict.Diagnostics
            .Where(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1843).ToList();
        Assert.Single(strict1843);
        Assert.Equal(AsyncNormalizeSeverity.Error, strict1843[0].Severity);

        var relaxed = Run(src, new AsyncNormalizeOptions { Strict = false });
        Assert.Contains(relaxed.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1843);
        // cfg 站点原样保留（带 skip 白名单），同文件无残余闸门。
        Assert.Equal(0, relaxed.Statistics.AsyncStreamResidualCount);
    }

    // ── 组 4：嵌套（序号独立）+ break/continue 原样 ──

    [Fact]
    public void Group04_NestedForeach_IndependentEnumeratorIndices()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var outer in new HandmadeEnumerable())
        {
            await foreach (var inner in new HandmadeEnumerable())
            {
                Console.WriteLine(outer + inner);
            }
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("__cs2jAsE_0", compact);
        Assert.Contains("__cs2jAsE_1", compact);
        Assert.Equal(2, result.Statistics.AwaitForeachExpanded);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group04_BreakContinue_PreservedVerbatim()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            if (x == 1) continue;
            if (x == 2) break;
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("continue;", compact);
        Assert.Contains("break;", compact);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 5：默认朴素 try/finally（net10 真值：finally 的 DisposeAsync 异常屏蔽
    //         在途主异常）+ 显式 FaithfulAsyncDisposeGuard=true 的四分支守卫 opt-in ──

    [Fact]
    public void Group05_DefaultIsPlainFinally_NoFlag_SingleTryFinally_NetTruth()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // 默认（net10 真值）：无 __cs2jAsOk_ 标志位、无 catch、单层 try/finally。
        Assert.DoesNotContain("__cs2jAsOk_", compact);
        Assert.DoesNotContain("catch", compact);
        Assert.Contains("finally{await__cs2jAsE_0.DisposeAsync();}", compact);
        // 守卫桶只属于 await using 的 null/HasValue 守卫；纯 foreach 文件恒 0。
        Assert.Equal(0, result.Statistics.AsyncDisposeGuardsEmitted);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group05_FaithfulGuardOptIn_FlagTryCatchInFlightBranch_WhenExplicitlyEnabled()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src, new AsyncNormalizeOptions { FaithfulAsyncDisposeGuard = true });

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("bool__cs2jAsOk_0=false;", compact);
        Assert.Contains("__cs2jAsOk_0=true;", compact);
        Assert.Contains("if(__cs2jAsOk_0){await__cs2jAsE_0.DisposeAsync();}", compact);
        Assert.Contains("else{try{await__cs2jAsE_0.DisposeAsync();}catch", compact);
        Assert.Contains("在途主异常优先", result.OutputCode);
        Assert.Contains("异步流遍历规则", result.OutputCode);
        // 空 catch（块内仅注释，在途分支吞掉释放异常）。
        Assert.Contains("catch{/*", compact);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 6：鸭子型 1840 / IAsyncEnumerator 直连基线豁免（无 18xx）/ 解构 1840 ──

    [Fact]
    public void Group06_DuckTypedSource_1840()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new DuckEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var strict = Run(src);
        Assert.False(strict.Changed);
        Assert.Contains(strict.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1840
            && d.Message.Contains("DuckEnumerable"));

        var relaxed = Run(src, new AsyncNormalizeOptions { Strict = false });
        Assert.Contains(relaxed.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1840);
        var compact = Compact(relaxed.OutputCode);
        Assert.Contains("awaitforeach(varxinnewDuckEnumerable())", compact);
        Assert.Equal(0, relaxed.Statistics.AsyncStreamResidualCount);
    }

    [Fact]
    public void Group06_AsyncEnumeratorDirect_BaselineExempt_No18xx()
    {
        var src = InClass(@"
    public async Task M()
    {
        var enumerator = new HandmadeEnumerable().GetAsyncEnumerator();
        await foreach (var x in enumerator)
        {
            Console.WriteLine(x);
        }
    }
");

        var model = ParseWithSemantics(src);
        Assert.Contains(model.Compilation.GetDiagnostics(), d => d.Id == "CS8411");

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1840
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1846
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
    }

    [Fact]
    public void Group06_DeconstructionForm_1840()
    {
        var src = InClass(@"
    public async Task M(IAsyncEnumerable<KeyValuePair<int, int>> pairs)
    {
        await foreach (var (k, v) in pairs)
        {
            Console.WriteLine(k);
        }
    }
");

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1840
            && d.Message.Contains("deconstruction", StringComparison.OrdinalIgnoreCase));
    }

    // ── 组 7：非 CT/非 BCL WithCancellation、重复 WithCancellation → CS2J1846 ──

    [Fact]
    public void Group07_NonBclWithCancellation_NonCtArgument_1846()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new CustomEnumerable().WithCancellation(7))
        {
            Console.WriteLine(x);
        }
    }
");

        var strict = Run(src);
        Assert.False(strict.Changed);
        Assert.Contains(strict.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1846);

        var relaxed = Run(src, new AsyncNormalizeOptions { Strict = false });
        Assert.Contains(relaxed.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1846);
        Assert.Equal(0, relaxed.Statistics.AsyncStreamResidualCount);
    }

    [Fact]
    public void Group07_RepeatedBclWithCancellation_1846()
    {
        var src = InClass(@"
    public async Task M(CancellationToken ct)
    {
        var src = new HandmadeEnumerable();
        await foreach (var x in src.WithCancellation(ct).WithCancellation(ct))
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1846
            && d.Message.Contains("repeated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1840);
    }

    // ── 组 8：显式元素类型 FQF / 泛型参数元素 / 方法链源只求值一次 ──

    [Fact]
    public void Group08_ExplicitValueTypeElement_RendersGlobalFqf()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (int x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // FullyQualifiedFormat 对内置特殊类型保留 C# 关键字（int），而非 global::System.Int32。
        Assert.Contains("intx=__cs2jAsE_0.Current;", compact);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group08_GenericTypeParameterElement_RendersAsIs()
    {
        var src = InClass(@"
    public async Task Consume<T>(IAsyncEnumerable<T> src)
    {
        await foreach (T x in src)
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // 方法类型参数原样渲染（不挂 global:: 前缀）。
        Assert.Contains("Tx=__cs2jAsE_0.Current;", compact);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group08_MethodChainSource_EvaluatedExactlyOnce()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in HandmadeEnumerable.GetSource().WithCancellation(CancellationToken.None))
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Equal(1, CountOccurrences(compact, "HandmadeEnumerable.GetSource()"));
        // WithCancellation 实参为 CancellationToken.None 时，token 表达式按原文转发
        // （仅裸源才补全局 global:: 形态）。
        Assert.Contains(
            "HandmadeEnumerable.GetSource().GetAsyncEnumerator(CancellationToken.None)",
            compact);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 9：AS-6 异步迭代器（方法/局部函数）严格 1844 整成员冻结；非严格仅该成员
    //         skip；kill-switch 关闭时不诊断但仍不展开（M4 前无归宿） ──

    [Fact]
    public void Group09_AsyncIteratorMethod_Strict_1844NoPartialRewrite()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        await foreach (var z in new HandmadeEnumerable())
        {
            yield return z;
        }
        yield break;
    }

    public async Task Consume()
    {
        await foreach (var x in Mk())
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
            && d.Message.Contains("M4"));
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
        Assert.Equal(0, result.Statistics.AwaitForeachExpanded);
    }

    [Fact]
    public void Group09_AsyncIteratorLocalFunction_Strict_1844()
    {
        var src = InClass(@"
    public async Task M()
    {
        async IAsyncEnumerable<int> Local()
        {
            yield return 1;
            await Task.CompletedTask;
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
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844
            && d.Message.Contains("local function"));
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
    }

    [Fact]
    public void Group09_AsyncIterator_NonStrict_MemberSkipped_ConsumerExpanded()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        await foreach (var z in new HandmadeEnumerable())
        {
            yield return z;
        }
    }

    public async Task Consume()
    {
        await foreach (var x in Mk())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        var compact = Compact(result.OutputCode);
        // Mk 体内站点原样冻结（恰好一处残留 await foreach 文本），消费侧已展开。
        Assert.Equal(1, CountOccurrences(compact, "awaitforeach"));
        Assert.Contains("yieldreturnz;", compact);
        Assert.Contains("Mk().GetAsyncEnumerator(", compact);
        Assert.Equal(1, result.Statistics.AsyncIteratorsRejected);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Group09_RejectAsyncIteratorDisabled_NoDiagnostic_StillNoExpansionInIterator()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Mk()
    {
        await foreach (var z in new HandmadeEnumerable())
        {
            yield return z;
        }
    }

    public async Task Consume()
    {
        await foreach (var x in Mk())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src, new AsyncNormalizeOptions { RejectAsyncIterator = false });

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.Equal(0, result.Statistics.AsyncIteratorsRejected);
        var compact = Compact(result.OutputCode);
        Assert.Equal(1, CountOccurrences(compact, "awaitforeach"));
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
    }

    // ── 组 10：选项隔离 ──

    [Fact]
    public void Group10_AsyncStreamsDisabled_ForeachAndUsingZeroRewrite()
    {
        var src = InClass(@"
    public async Task M()
    {
        await using var r = new MyRes();
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var result = Run(src, new AsyncNormalizeOptions { EnableAsyncStreams = false });

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.AwaitUsingStatementsExpanded);
        Assert.Equal(0, result.Statistics.AwaitUsingDeclarationsExpanded);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1841
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
    }

    [Fact]
    public void Group10_AwaitForeachDisabled_SiteAndParam1841_IAsyncDisposableParamPasses()
    {
        var src = InClass(@"
    public async Task M(IAsyncEnumerable<int> seq, IAsyncDisposable resource)
    {
        await foreach (var x in seq)
        {
            Console.WriteLine(x);
        }
        await using var r = new MyRes();
    }
");

        var result = Run(src, new AsyncNormalizeOptions { EnableAwaitForeach = false });

        Assert.False(result.Changed);
        var errors1841 = result.Diagnostics
            .Where(d => d.Severity == AsyncNormalizeSeverity.Error
                && d.Code == AsyncNormalizeDiagnosticCode.CS2J1841).ToList();
        // foreach 站点 1 处 + 全文件异步枚举类型使用点 3 处：M 的 seq 形参、
        // HandmadeEnumerable.GetSource 的 IAsyncEnumerable 返回类型、
        // CustomEnumerable.WithCancellation 的 CustomEnumerable 返回类型（实现
        // IAsyncEnumerable）。注意 IAsyncEnumerator<T> 本身派生 IAsyncDisposable，
        // 两个 GetAsyncEnumerator 返回类型点走 3a 恒放行面，不计 1841。
        Assert.Equal(4, errors1841.Count);
        // 放行 3：IAsyncDisposable 形参 + 两处 IAsyncEnumerator 返回类型（按 3a 面计）。
        Assert.Equal(3, result.Statistics.AsyncTypeSitesMapped);
        // 3 处类型点拒绝；foreach 站点拒绝走站点桶，不进类型点桶。
        Assert.Equal(3, result.Statistics.AsyncTypeSitesRejected);
        Assert.Equal(0, result.Statistics.AwaitForeachExpanded);
        // await using 通道独立但严格 1841 整文件短路：展开桶为 0，未改写的 await using
        // 站点被 residual 闸门捕获（同 C18 CS2J1810 纪律；await foreach 因门关闭不计数）。
        Assert.Equal(0, result.Statistics.AwaitUsingDeclarationsExpanded);
        Assert.Equal(1, result.Statistics.ResidualCount);
        Assert.Equal(1, result.Statistics.AsyncStreamResidualCount);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
    }

    // ── 组 11：混合文件——C18 lambda 提升 + Task ConfigureAwait 剥离 +
    //         await foreach + await using，桶各归各、零残余 ──

    [Fact]
    public void Group11_MixedFile_C18AndC38_BucketsPartition_ZeroResidual()
    {
        var src = InClass(@"
    public async Task M()
    {
        Func<Task> f = async () => { await Task.Delay(1).ConfigureAwait(false); };
        await Task.Run(() => Console.WriteLine(""z"")).ConfigureAwait(false);
        await using var r = new MyRes();
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
        await f();
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", compact);
        Assert.DoesNotContain("awaitusing", compact);
        Assert.DoesNotContain("awaitforeach", compact);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(2, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(1, result.Statistics.AwaitUsingDeclarationsExpanded);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 12：非严格降级 + 确定性/幂等/防撞/无 <> 名 ──

    [Fact]
    public void Group12_NonStrict_1840_1843_1844_1846_AllDowngradeWarning_AndValidSitesExpand()
    {
        var src = InClass(@"
    public async IAsyncEnumerable<int> Iterator()
    {
        yield return 1;
        await foreach (var z in new HandmadeEnumerable()) { yield return z; }
    }

    public async Task M(IAsyncEnumerable<int> seq, CancellationToken ct)
    {
        ConfiguredCancelableAsyncEnumerable<int> cfg = seq.WithCancellation(ct);
        await foreach (var a in cfg) Console.WriteLine(a);                              // 1843
        await foreach (var b in new DuckEnumerable()) Console.WriteLine(b);             // 1840
        await foreach (var c in new CustomEnumerable().WithCancellation(7)) Console.WriteLine(c); // 1846
        await foreach (var d in seq) Console.WriteLine(d);                               // 合法照展开
    }
");

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning && d.Code == AsyncNormalizeDiagnosticCode.CS2J1840);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning && d.Code == AsyncNormalizeDiagnosticCode.CS2J1843);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning && d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning && d.Code == AsyncNormalizeDiagnosticCode.CS2J1846);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Group12_Idempotent_Deterministic_NoCompilerGeneratedNames()
    {
        var src = InClass(@"
    public async Task M()
    {
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
    }
");

        var first = Run(src);
        Assert.True(first.Changed);
        Assert.DoesNotContain(first.OutputCode, "<>");

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);

        var deterministic = Run(src);
        Assert.Equal(first.OutputCode, deterministic.OutputCode);
    }

    [Fact]
    public void Group12_TempNameCollision_RenamesEnumeratorWithSuffix()
    {
        var src = InClass(@"
    public async Task M()
    {
        var __cs2jAsE_0 = 5;
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x + __cs2jAsE_0);
        }
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2jAsE_02=", compact);
        Assert.DoesNotContain(compact, "var__cs2jAsE_0=");
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 组 13：span 漂移硬化——C18 ConfigureAwait 剥离 / 零捕获 lambda 提升发生在
    //         同块 await using 与 await foreach 之前；后序站点全部在 post-C18 树
    //         展开（含被提升合成方法体内的 await foreach），零 1849，非严格也不漏 ──

    [Fact]
    public void Group13_SpanDrift_C18TransformsBeforeStreamSites_AllExpand_Strict()
    {
        var src = InClass(@"
    public async Task M()
    {
        Func<Task> liftedPlain = async () => { await Task.Delay(1).ConfigureAwait(false); };
        Func<Task> liftedWithForeach = async () =>
        {
            await foreach (var y in new HandmadeEnumerable())
            {
                Console.WriteLine(y);
            }
        };
        await Task.Run(() => Console.WriteLine(""z"")).ConfigureAwait(false);
        await using var r = new MyRes();
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
        await liftedPlain();
        await liftedWithForeach();
    }
");

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", compact);
        Assert.DoesNotContain("awaitusing", compact);
        Assert.DoesNotContain("awaitforeach", compact);
        // 外层 1 + 提升合成方法体内 1（只数调用点 ".GetAsyncEnumerator("，夹具里的
        // 三个接口实现声明无先导点号，不计入）。
        Assert.Equal(2, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(2, CountOccurrences(compact, ".GetAsyncEnumerator("));
        Assert.Equal(1, result.Statistics.AwaitUsingDeclarationsExpanded);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Group13_SpanDrift_NonStrict_SkipTaggedSitesNoResidual()
    {
        var src = InClass(@"
    public async Task M()
    {
        Func<Task> f = async () => { await Task.Delay(1).ConfigureAwait(false); };
        await Task.Run(() => Console.WriteLine(""z"")).ConfigureAwait(false);
        await foreach (var bad in new DuckEnumerable()) Console.WriteLine(bad);
        await using var r = new MyRes();
        await foreach (var x in new HandmadeEnumerable())
        {
            Console.WriteLine(x);
        }
        await f();
    }
");

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1840);
        Assert.Equal(1, result.Statistics.AwaitForeachExpanded);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(0, result.Statistics.AsyncStreamResidualCount);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── Helpers（夹具同 AsyncStreamAwaitUsingUnitTests） ──

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

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-stream-foreach-probe",
            new[] { tree },
            References.Value,
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
            "async-stream-foreach-test",
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
