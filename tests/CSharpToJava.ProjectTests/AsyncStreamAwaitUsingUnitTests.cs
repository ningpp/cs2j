using System.Text.RegularExpressions;
using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C38 M2/3a：<c>await using</c> → try/finally + <c>await DisposeAsync()</c> 归一
/// （设计 §7.1 测试组 1-17、§4.3 七条细则、D4/D5/D8/D10/D11）。夹具同
/// AsyncNormalizeUnitTests：Latest 语法 + TPA 引用，每个展开用例断言输出重解析无 Error。
/// </summary>
public class AsyncStreamAwaitUsingUnitTests
{
    // 全套夹具类型：接口 class/struct、鸭子 class、ref struct 鸭子、双实现、显式实现、可空结构。
    private const string TypesPreamble = @"
using System;
using System.Threading.Tasks;

class MyRes : IAsyncDisposable
{
    public ValueTask DisposeAsync() => default;
    public static MyRes Make() => new MyRes();
    public static ValueTask<MyRes> MakeAsync() => new ValueTask<MyRes>(new MyRes());
}

struct MyStructRes : IAsyncDisposable
{
    public ValueTask DisposeAsync() => default;
}

class DuckRes
{
    public ValueTask DisposeAsync() => default;
}

ref struct RefStructRes
{
    public ValueTask DisposeAsync() => default;
}

class DualRes : IDisposable, IAsyncDisposable
{
    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
}

class ExplicitRes : IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync() => default;
}

class C
{
";

    private const string TypesSuffix = @"
}
";

    // ── 组 1：语句形态基本展开 ──

    [Fact]
    public void Group01_StatementForm_BasicTryFinallyWithNullGuard()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var r = new MyRes())
        {
            Console.WriteLine(""body"");
        }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("try", compact);
        Assert.Contains("finally", compact);
        Assert.Contains("if(r!=null)awaitr.DisposeAsync();", compact);
        Assert.DoesNotContain("awaitusing", compact);
        var bodyIndex = compact.IndexOf("Console.WriteLine(\"body\")", StringComparison.Ordinal);
        var disposeIndex = compact.IndexOf("awaitr.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(bodyIndex >= 0 && disposeIndex > bodyIndex);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 2：声明形态——后继语句全部进入 try，释放位于块末 finally ──

    [Fact]
    public void Group02_DeclarationForm_FoldsTailStatementsIntoTry()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using var r = new MyRes();
        Console.WriteLine(""a"");
        Console.WriteLine(""b"");
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("varr=newMyRes();try{", compact);
        var a = compact.IndexOf("Console.WriteLine(\"a\")", StringComparison.Ordinal);
        var b = compact.IndexOf("Console.WriteLine(\"b\")", StringComparison.Ordinal);
        var dispose = compact.IndexOf("awaitr.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a && dispose > b);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 3：连续两个 using 声明——LIFO 反向嵌套（b 先释放、a 后释放） ──

    [Fact]
    public void Group03_TwoDeclarations_LifoNestedDisposal()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using var a = new MyRes();
        await using var b = new MyRes();
        Console.WriteLine(a.ToString());
        Console.WriteLine(b.ToString());
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        var initA = compact.IndexOf("vara=newMyRes()", StringComparison.Ordinal);
        var initB = compact.IndexOf("varb=newMyRes()", StringComparison.Ordinal);
        var disposeB = compact.IndexOf("awaitb.DisposeAsync()", StringComparison.Ordinal);
        var disposeA = compact.IndexOf("awaita.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(initA >= 0 && initB > initA);
        Assert.True(disposeB >= 0 && disposeA > disposeB);
        Assert.Equal(2, CountOccurrences(compact, "finally"));
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 4：语句形态 + 声明形态 + 同步 using 混排——同步 using 原样不动 ──

    [Fact]
    public void Group04_MixedForms_SyncUsingUntouched()
    {
        var src = TypesPreamble + @"
    public void SyncWork() { }
    public void More() { }
    public void After() { }

    public async Task M()
    {
        using (var s = new DualRes())
        {
            SyncWork();
        }
        await using var r = new MyRes();
        await using (var q = new MyRes())
        {
            More();
        }
        After();
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // 同步 using 语句原样保留。
        Assert.Contains("using(vars=newDualRes())", compact);
        Assert.DoesNotContain("awaits.DisposeAsync", compact);
        // 两个 await using 均已展开。
        Assert.Contains("awaitr.DisposeAsync()", compact);
        Assert.Contains("awaitq.DisposeAsync()", compact);
        Assert.DoesNotContain("awaitusing", compact);
        // 后继 After() 被包进声明形态 try 内，位于 r 释放之前。
        Assert.True(compact.IndexOf("After()", StringComparison.Ordinal)
            < compact.IndexOf("awaitr.DisposeAsync()", StringComparison.Ordinal));
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 5：纯表达式资源 → __cs2jAsRes_0 只求值一次 ──

    [Fact]
    public void Group05_ExpressionResource_AllocatedTempName()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (MyRes.Make())
        {
            Console.WriteLine(""x"");
        }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2jAsRes_0=MyRes.Make();", compact);
        Assert.Equal(1, CountOccurrences(compact, "MyRes.Make()"));
        Assert.Contains("if(__cs2jAsRes_0!=null)await__cs2jAsRes_0.DisposeAsync();", compact);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 6：语句形态单类型多变量——两层 LIFO 嵌套 ──

    [Fact]
    public void Group06_MultiVariableStatement_TwoNestedLevels()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (MyRes a = new MyRes(), b = new MyRes())
        {
            Console.WriteLine(""both"");
        }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        var initA = compact.IndexOf("MyResa=newMyRes()", StringComparison.Ordinal);
        var initB = compact.IndexOf("MyResb=newMyRes()", StringComparison.Ordinal);
        var disposeB = compact.IndexOf("awaitb.DisposeAsync()", StringComparison.Ordinal);
        var disposeA = compact.IndexOf("awaita.DisposeAsync()", StringComparison.Ordinal);
        Assert.True(initA >= 0 && initB > initA);
        Assert.True(disposeB >= 0 && disposeA > disposeB);
        Assert.Equal(2, CountOccurrences(compact, "try"));
        Assert.Equal(2, CountOccurrences(compact, "finally"));
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 7：class 发 null 守卫；struct 直接调用；S? 发 HasValue/.Value 守卫 ──

    [Fact]
    public void Group07_ClassGuard_StructDirect_NullableStructHasValueGuard()
    {
        var classSrc = TypesPreamble + @"
    public async Task ClassCase()
    {
        await using (var r = new MyRes()) { }
    }
" + TypesSuffix;
        var structSrc = TypesPreamble + @"
    public async Task StructCase()
    {
        await using (var s = new MyStructRes()) { }
    }
" + TypesSuffix;
        var nullableSrc = TypesPreamble + @"
    public async Task NullableCase()
    {
        await using (MyStructRes? n = new MyStructRes()) { }
    }
" + TypesSuffix;

        var classResult = Run(classSrc);
        var structResult = Run(structSrc);
        var nullableResult = Run(nullableSrc);

        Assert.True(classResult.Changed);
        Assert.Contains("if(r!=null)awaitr.DisposeAsync();", Compact(classResult.OutputCode));

        Assert.True(structResult.Changed);
        var structCompact = Compact(structResult.OutputCode);
        Assert.Contains("awaits.DisposeAsync();", structCompact);
        Assert.DoesNotContain("if(s!=null)", structCompact);

        Assert.True(nullableResult.Changed);
        var nullableCompact = Compact(nullableResult.OutputCode);
        Assert.Contains("if(n.HasValue)awaitn.Value.DisposeAsync();", nullableCompact);

        AssertOutputParsesClean(classResult.OutputCode);
        AssertOutputParsesClean(structResult.OutputCode);
        AssertOutputParsesClean(nullableResult.OutputCode);
    }

    // ── 组 8：空体仍发完整 try/finally ──

    [Fact]
    public void Group08_EmptyBody_FullTryFinally()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var r = new MyRes()) { }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("try{}finally{if(r!=null)awaitr.DisposeAsync();}", compact);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 9：await FactoryAsync() 只求值一次且初始化（含 await）位于 try 外 ──

    [Fact]
    public void Group09_DeclarationForm_AsyncFactoryAwaitedOnceOutsideTry()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using var r = await MyRes.MakeAsync();
        Console.WriteLine(r.ToString());
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Equal(1, CountOccurrences(compact, "MyRes.MakeAsync()"));
        var factory = compact.IndexOf("awaitMyRes.MakeAsync()", StringComparison.Ordinal);
        var tryIndex = compact.IndexOf("try", StringComparison.Ordinal);
        Assert.True(factory >= 0 && tryIndex > factory);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 10：用户既有 try/catch/finally 不与合成释放区串区 ──

    [Fact]
    public void Group10_UserTryCatchFinally_DoesNotCrossZones()
    {
        var src = TypesPreamble + @"
    public void Inside() { }
    public void Caught() { }
    public void Cleanup() { }
    public void After() { }

    public async Task M()
    {
        await using var r = new MyRes();
        try
        {
            Inside();
        }
        catch (Exception ex)
        {
            Caught();
        }
        finally
        {
            Cleanup();
        }
        After();
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var output = result.OutputCode;
        Assert.Contains("catch (Exception ex)", output);
        Assert.Equal(2, CountOccurrences(Compact(output), "finally"));
        var compact = Compact(output);
        Assert.True(compact.IndexOf("Cleanup()", StringComparison.Ordinal)
            < compact.IndexOf("awaitr.DisposeAsync()", StringComparison.Ordinal));
        AssertOutputParsesClean(output);
    }

    // ── 组 11：双实现类型——finally 只见 DisposeAsync、不见 Dispose()，并发 CS2J1830 Info ──

    [Fact]
    public void Group11_DualImplementation_BindsDisposeAsyncOnly_WithInfo()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var d = new DualRes())
        {
            Console.WriteLine(""dual"");
        }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("awaitd.DisposeAsync();", compact);
        Assert.DoesNotContain("d.Dispose()", compact);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Info
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1830);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 12：鸭子型/ref struct ──
    // 12a 严格：Error CS2J1842、整文件不改写；12b/12c 非严格：Warning、
    // 问题站点原样保留（白名单）、同文件合法站点照常展开。

    [Fact]
    public void Group12a_DuckType_Strict_ErrorAndNoRewrite()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var bad = new DuckRes()) { }
    }
" + TypesSuffix;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1842
            && d.Message.Contains("DuckRes"));
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    [Fact]
    public void Group12b_DuckType_NonStrict_WarningButOtherSitesExpand()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var bad = new DuckRes()) { Console.WriteLine(""x""); }
        await using (var good = new MyRes()) { Console.WriteLine(""y""); }
    }
" + TypesSuffix;

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1842
            && d.Message.Contains("DuckRes"));
        var compact = Compact(result.OutputCode);
        // 问题站点原样保留。
        Assert.Contains("awaitusing(varbad=newDuckRes())", compact);
        // 同文件合法站点照常展开。
        Assert.Contains("awaitgood.DisposeAsync()", compact);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    [Fact]
    public void Group12c_RefStruct_NonStrict_SameGateAndOtherSitesExpand()
    {
        var src = TypesPreamble + @"
    public async Task M()
    {
        await using (var bad = new RefStructRes()) { Console.WriteLine(""x""); }
        await using (var good = new MyRes()) { Console.WriteLine(""y""); }
    }
" + TypesSuffix;

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1842
            && d.Message.Contains("ref struct"));
        var compact = Compact(result.OutputCode);
        Assert.Contains("awaitusing(varbad=newRefStructRes())", compact);
        Assert.Contains("awaitgood.DisposeAsync()", compact);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 13：EnableAwaitUsing=false → CS2J1841 原文保留；IAsyncDisposable 形参放行计数 ──

    [Fact]
    public void Group13_DisabledOption_1841PreservesSite_InterfaceParameterMapped()
    {
        var src = TypesPreamble + @"
    public async Task M(IAsyncDisposable p)
    {
        await using (var r = new MyRes())
        {
            Console.WriteLine(p.ToString());
        }
    }
" + TypesSuffix;

        var result = Run(src, new AsyncNormalizeOptions { EnableAwaitUsing = false });

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1841);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1842);
        Assert.Equal(1, result.Statistics.AsyncTypeSitesMapped);
        Assert.Equal(1, result.Statistics.AsyncTypeSitesRejected);
        Assert.Equal(0, result.Statistics.AwaitUsingStatementsExpanded);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    // ── 组 14：非 async 方法——基线 CS4033 已报，豁免不发任何 18xx，也不发 residual 1849 ──

    [Fact]
    public void Group14_NonAsyncMethod_BaselineExempt_No18xx()
    {
        var src = TypesPreamble + @"
    public void M()
    {
        await using (var r = new MyRes()) { }
    }
" + TypesSuffix;

        var model = ParseWithSemantics(src);
        Assert.Contains(model.Compilation.GetDiagnostics(), d => d.Id == "CS4033");

        var result = new AsyncNormalizePreprocessor().Preprocess(src, model, new AsyncNormalizeOptions());

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1841
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1842
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1845
            || d.Code == AsyncNormalizeDiagnosticCode.CS2J1849);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    // ── 组 15：幂等（二次 Changed=false 且文本一致）+ 用户占用 __cs2jAsRes_0 防撞 ──

    [Fact]
    public void Group15_Idempotent_AndUserTempNameCollisionAvoided()
    {
        var idempotentSrc = TypesPreamble + @"
    public async Task M()
    {
        await using (var r = new MyRes())
        {
            Console.WriteLine(""once"");
        }
    }
" + TypesSuffix;

        var first = Run(idempotentSrc);
        Assert.True(first.Changed);
        AssertOutputParsesClean(first.OutputCode);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);

        var collisionSrc = TypesPreamble + @"
    public async Task Collision()
    {
        var __cs2jAsRes_0 = new MyRes();
        await using (MyRes.Make())
        {
            Console.WriteLine(__cs2jAsRes_0.ToString());
        }
    }
" + TypesSuffix;

        var collision = Run(collisionSrc);
        Assert.True(collision.Changed);
        var compact = Compact(collision.OutputCode);
        // 原名末尾追加 2 让位（AsyncNormalizeNaming.ResolveUniqueName 手法）。
        Assert.Contains("var__cs2jAsRes_02=MyRes.Make();", compact);
        Assert.Contains("await__cs2jAsRes_02.DisposeAsync()", compact);
        AssertOutputParsesClean(collision.OutputCode);
    }

    // ── 组 16：EnableAsyncStreams=false——C38 零改写零诊断，C18 ConfigureAwait 回归照常 ──

    [Fact]
    public void Group16_AsyncStreamsDisabled_ZeroC38Rewrite_C18ConfigureAwaitStillStripped()
    {
        var src = TypesPreamble + @"
    public async Task M(System.Threading.Tasks.Task t)
    {
        await using (var r = new MyRes()) { Console.WriteLine(""x""); }
        await t.ConfigureAwait(false);
    }
" + TypesSuffix;

        var result = Run(src, new AsyncNormalizeOptions { EnableAsyncStreams = false });

        Assert.True(result.Changed);
        Assert.Contains("await using (", result.OutputCode);
        Assert.DoesNotContain("await r.DisposeAsync", result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1841);
        Assert.Equal(0, result.Statistics.AwaitUsingStatementsExpanded);
        AssertOutputParsesClean(result.OutputCode);
    }

    // ── 组 17：6 个 C38 统计桶精确断言（非严格：2 语句 + 1 声明 + 1 鸭子拒绝 + 1 形参放行） ──

    [Fact]
    public void Group17_StatisticsBuckets_PreciseCounts()
    {
        var src = TypesPreamble + @"
    public async Task M(IAsyncDisposable p)
    {
        await using (var r1 = new MyRes()) { Console.WriteLine(""a""); }
        await using var r2 = new MyRes();
        Console.WriteLine(r2.ToString());
        await using (var s = new MyStructRes()) { Console.WriteLine(""c""); }
        await using (var bad = new DuckRes()) { Console.WriteLine(""d""); }
        Console.WriteLine(p.ToString());
    }
" + TypesSuffix;

        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.AwaitUsingStatementsExpanded);
        Assert.Equal(1, result.Statistics.AwaitUsingDeclarationsExpanded);
        Assert.Equal(2, result.Statistics.AsyncDisposeGuardsEmitted);
        Assert.Equal(1, result.Statistics.AsyncTypeSitesMapped);
        Assert.Equal(0, result.Statistics.AsyncTypeSitesRejected);
        Assert.Equal(1, result.Statistics.DuckTypedAsyncDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        AssertOutputParsesClean(result.OutputCode);
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
            "async-stream-test",
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
