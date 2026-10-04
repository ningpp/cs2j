using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C18 async-normalize M1b：async lambda 四型提升（D3）、残余 async 局部函数归宿（D4）、
/// CS2J1802/1804/1807 诊断与 residual 闸门白名单（设计 §7.1 第 1-14 组）。
/// 全部用例：内联源码 → TPA 引用编译 → Preprocess → 文本/统计断言 + 输出零 parse Error；
/// 正例额外断言输出零编译 Error（提升方法签名/泛型携带/碰撞消解真实可编译）。
/// </summary>
public class AsyncNormalizeLambdaUnitTests
{
    // ── 1. 零捕获 Func<Task>：static 提升 + 显式委托调用点 ──

    [Fact]
    public void Lambda_ZeroCapture_FuncTask_LiftsToStaticMethod()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        await Task.Run(async () => await Task.CompletedTask);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("private static async global::System.Threading.Tasks.Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task>(Cs2jAsync_Run_0)",
            result.OutputCode);
        // 原 lambda 已整棵消失（提升方法为块体，不存在 async () 形态）。
        Assert.DoesNotContain("async ()", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedThisOnly);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedVoidWrapper);
        Assert.Equal(0, result.Statistics.AsyncLocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.WrappersGenerated);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 2. §2.1 示意：Task.Run(async () => await Task.FromResult(Compute())) ──

    [Fact]
    public void Lambda_Section2_1Shape_FromResultCompute()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    private static int Compute() => 42;

    public async Task RunAsync()
    {
        await Task.Run(async () => await Task.FromResult(Compute()));
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("Cs2jAsync_RunAsync_0", result.OutputCode);
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task<int>>(Cs2jAsync_RunAsync_0)",
            result.OutputCode);
        Assert.Contains("return await Task.FromResult(Compute());", result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 3. 仅捕获 this：实例方法（无 static 修饰） ──

    [Fact]
    public void Lambda_ThisOnly_LiftsToInstanceMethod()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    private async Task RefreshAsync(int count)
    {
        await Task.CompletedTask;
    }

    public async Task Run()
    {
        await Task.Run(async () => await RefreshAsync(7));
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("private async global::System.Threading.Tasks.Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.DoesNotContain("private static async global::System.Threading.Tasks.Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedThisOnly);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 4. void 返回委托（事件式）：Task 方法 + _w wrapper + new Action 调用点 ──

    [Fact]
    public void Lambda_VoidDelegate_ActionEvent_WrapperPair()
    {
        const string src = @"
using System.Threading.Tasks;
class TickArgs
{
    public int Count { get; set; }
}
class C
{
    public event System.Action<object, TickArgs> Tick;

    private async Task RefreshAsync(int count)
    {
        await Task.CompletedTask;
    }

    public async Task Run()
    {
        Tick += async (s, e) => await RefreshAsync(e.Count);
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        // 调用点指向 wrapper（System.Object 在 FQF 下按 Roslyn 特殊类型规则渲染为 object 关键字）。
        Assert.Contains(
            "new global::System.Action<object, global::TickArgs>(Cs2jAsync_Run_0_w)",
            result.OutputCode);
        // 提升的 Task 方法（实例，因捕获 this）。
        Assert.Contains(
            "private async global::System.Threading.Tasks.Task Cs2jAsync_Run_0(object s, global::TickArgs e)",
            result.OutputCode);
        Assert.Contains("await RefreshAsync(e.Count);", result.OutputCode);
        // wrapper：private void（非 async）+ fire-and-forget 转发。
        Assert.Contains(
            "private void Cs2jAsync_Run_0_w(object s, global::TickArgs e)",
            result.OutputCode);
        Assert.Contains("Cs2jAsync_Run_0(s, e);", result.OutputCode);
        // 禁止生成 async void。
        Assert.DoesNotContain("async void", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedVoidWrapper);
        Assert.Equal(1, result.Statistics.WrappersGenerated);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedThisOnly);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 5. Func<Task<int>> 表达式体：提升后转 return 语句 ──

    [Fact]
    public void Lambda_FuncTaskOfInt_ExpressionBody_WrappedAsReturn()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        await Task.Run(async () => await Task.FromResult(1));
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task<int> Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.Contains("return await Task.FromResult(1);", result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 6a. 捕获局部/形参：严格 CS2J1802 Error + 整体不改写 ──

    [Fact]
    public void Lambda_CapturesLocal_Strict_Error1802_NoRewrite()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run(int state)
    {
        await Task.Run(async () => await Task.FromResult(state));
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.Equal(1, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        // 严格模式 residual 闸门对未提升 lambda 发射 CS2J1810（设计 §7.3：无法提升+严格 → pass 失败）。
        Assert.Equal(1, result.Statistics.ResidualCount);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1810);
    }

    // ── 6b. 非严格：1802 降 Warning、节点保留、白名单使 residual=0 ──

    [Fact]
    public void Lambda_CapturesLocal_NonStrict_Warning_WhitelistedResidualZero()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run(int state)
    {
        await Task.Run(async () => await Task.FromResult(state));
    }
}";
        var result = Run(src, new AsyncNormalizeOptions { Strict = false });

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == AsyncNormalizeSeverity.Error);
        Assert.Equal(1, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1810);
    }

    // ── 7. 并列/嵌套序号确定性 + 二次 Preprocess 幂等 + 双跑字节一致 ──

    [Fact]
    public void Lambda_SiblingAndNested_OrderingDeterministic_AndIdempotent()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        await Task.Run(async () => await Task.CompletedTask);
        await Task.Run(async () => await Task.FromResult(5));
        await Task.Run(async () =>
        {
            await Task.Run(async () => await Task.CompletedTask);
        });
    }
}";
        var first = Run(src);
        var again = Run(src);

        Assert.True(first.Changed);
        // 双跑字节一致（后序枚举：并列按 span、嵌套内层先编号）。
        Assert.Equal(first.OutputCode, again.OutputCode);

        // 序号：并列第一=0（Task）、并列第二=1（Task<int>）、嵌套内层=2、外层=3。
        Assert.Contains("global::System.Threading.Tasks.Task Cs2jAsync_Run_0()", first.OutputCode);
        Assert.Contains("global::System.Threading.Tasks.Task<int> Cs2jAsync_Run_1()", first.OutputCode);
        // 外提方法体内的内层调用点 + Run 里外外层调用点。
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task>(Cs2jAsync_Run_2)",
            first.OutputCode);
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task>(Cs2jAsync_Run_3)",
            first.OutputCode);
        Assert.Equal(4, first.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, first.Statistics.ResidualCount);

        // 幂等：二次 Preprocess 无改写、字节不变。
        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.AsyncLambdasLiftedZeroCapture);

        AssertOutputParsesClean(first.OutputCode);
        AssertOutputCompiles(first.OutputCode);
    }

    // ── 8. 泛型方法：外层 T 携带 + where 约束 + 调用点显式 <T> ──

    [Fact]
    public void Lambda_GenericMethod_TypeParameterAndConstraintCarried()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task M<T>(T x) where T : class
    {
        await Task.Run(async () => await Task.FromResult(default(T)));
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task<T> Cs2jAsync_M_0<T>()",
            result.OutputCode);
        Assert.Contains("where T : class", result.OutputCode);
        Assert.Contains(
            "new global::System.Func<global::System.Threading.Tasks.Task<T>>(Cs2jAsync_M_0<T>)",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 9a. 残余 async 局部函数零捕获：提升 + 声明删除 + 调用点改写 ──

    [Fact]
    public void LocalFunction_ZeroCapture_Lifted_DeclarationRemoved_CallSiteRewritten()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        async Task Local()
        {
            await Task.CompletedTask;
        }

        await Local();
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        // 局部函数返回类型按源语法渲染（源码为 Task，非 FQN；using 在文件内可编译）。
        Assert.Contains(
            "private static async Task Cs2jAsync_Run_0()",
            result.OutputCode);
        Assert.Contains("await Cs2jAsync_Run_0();", result.OutputCode);
        // 原局部函数声明已删除。
        Assert.DoesNotContain("async Task Local()", result.OutputCode);

        Assert.Equal(1, result.Statistics.AsyncLocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 9b. 残余 async 局部函数捕获外层变量：CS2J1804 严格 Error，不改写 ──

    [Fact]
    public void LocalFunction_CapturesLocal_Strict_Error1804_NoRewrite()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run(int seed)
    {
        async Task Local()
        {
            await Task.FromResult(seed);
        }

        await Local();
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1804);
        Assert.Equal(0, result.Statistics.AsyncLocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    // ── 10. async 匿名方法（async delegate）：CS2J1807 ──

    [Fact]
    public void AsyncAnonymousMethod_Strict_Error1807_NonStrictWhitelisted()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        await Task.Run(async delegate { await Task.Yield(); });
    }
}";
        var strict = Run(src);

        Assert.False(strict.Changed);
        Assert.Equal(src, strict.OutputCode);
        Assert.Contains(strict.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1807);
        Assert.Equal(1, strict.Statistics.FilesDirtyNoRewrite);

        var nonStrict = Run(src, new AsyncNormalizeOptions { Strict = false });
        Assert.False(nonStrict.Changed);
        Assert.Contains(nonStrict.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Warning
            && d.Code == AsyncNormalizeDiagnosticCode.CS2J1807);
        Assert.DoesNotContain(nonStrict.Diagnostics, d =>
            d.Severity == AsyncNormalizeSeverity.Error);
        Assert.Equal(0, nonStrict.Statistics.ResidualCount);
    }

    // ── 11. kill-switch：lambda 原样（无 1802）、ConfigureAwait 仍剥离 ──

    [Fact]
    public void KillSwitch_LambdaKept_ConfigureAwaitStillStripped()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task M(int x)
    {
        await Task.Run(async () => await Task.FromResult(x).ConfigureAwait(false));
    }
}";
        var result = Run(src, new AsyncNormalizeOptions { LiftAsyncLambdas = false });

        Assert.True(result.Changed);
        Assert.Contains("async () =>", result.OutputCode);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1802);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 12. 提升方法体内的 ConfigureAwait(false) 同步被剥 ──

    [Fact]
    public void ConfigureAwait_InsideLiftedBody_IsStripped()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task Run()
    {
        await Task.Run(async () => await Task.CompletedTask.ConfigureAwait(false));
    }
}";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(1, result.Statistics.ConfigureAwaitStripped);
        Assert.DoesNotContain("ConfigureAwait", result.OutputCode);
        Assert.Contains("await Task.CompletedTask;", result.OutputCode);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 13. 预声明同名成员碰撞：追加 2 后缀消解 ──

    [Fact]
    public void PreexistingMemberName_Collision_AppendsNumericSuffix()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task M()
    {
        await Task.Run(async () => await Task.CompletedTask);
    }

    private static Task Cs2jAsync_M_0() => Task.CompletedTask;
}";
        var result = Run(src);

        Assert.True(result.Changed);
        // 预声明 Cs2jAsync_M_0 保留，新提升方法消解为 Cs2jAsync_M_02。
        Assert.Contains("private static Task Cs2jAsync_M_0()", result.OutputCode);
        Assert.Contains(
            "private static async global::System.Threading.Tasks.Task Cs2jAsync_M_02()",
            result.OutputCode);
        Assert.Contains("(Cs2jAsync_M_02)", result.OutputCode);
        Assert.DoesNotContain("(Cs2jAsync_M_0)", result.OutputCode);
        Assert.Equal(1, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.ResidualCount);
        AssertOutputParsesClean(result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── 14. 统计桶：正常 Task 方法 + 零提升时其余桶逐项为 0（指纹口径） ──

    [Fact]
    public void Statistics_PlainAsyncMethod_LiftBucketsAllZero()
    {
        const string src = @"
using System.Threading.Tasks;
class C
{
    public async Task M()
    {
        await Task.Delay(1);
    }
}";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedZeroCapture);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedThisOnly);
        Assert.Equal(0, result.Statistics.AsyncLambdasLiftedVoidWrapper);
        Assert.Equal(0, result.Statistics.AsyncLocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.WrappersGenerated);
        Assert.Equal(0, result.Statistics.CapturedAsyncLambdasDeferred);
        Assert.Equal(0, result.Statistics.ConfigureAwaitStripped);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(1, result.Statistics.AsyncMethodsSeen);
        Assert.Equal(1, result.Statistics.AwaitSites);
    }

    // ── Helpers（TPA references + Latest + allowUnsafe，对齐 1a 测试夹具） ──

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

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-normalize-lambda-output",
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
            "async-normalize-lambda-test",
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
