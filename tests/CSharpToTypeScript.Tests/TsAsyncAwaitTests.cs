using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：async/await → Promise 发射（spec §1.4.5）。C#→Java 共享管线把 Task 映射为
/// CompletableFuture 并降级 await → `.join()`，TsAsyncRewriter 在 IR 层统一为原生
/// Promise + async/await：
/// ① async 方法 → `async name(...): Promise&lt;T&gt;`（TsMemberMappingTable 映射
///    CompletableFuture → Promise，TsMemberWriter 发射 `async ` 前缀）；
/// ② async 方法体三路改写（Body 字符串 / raw 语句 / 结构化语句表达式）：
///    `.join()` → `await X`（跳过 String.join/StringHelper.join）、
///    `return CompletableFuture.completedFuture(X);` → `return X;`（null → `return;`）；
/// ③ 全方法体（含非 async 的 Task 返回方法与字段初始化器）：
///    completedFuture → Promise.resolve、delayedExecutor（Task.Delay）→
///    new Promise(resolve => setTimeout)、yield（Task.Yield）→ Promise.resolve、
///    allOf（Task.WhenAll）→ Promise.all、runAsync/supplyAsync（Task.Run）→
///    Promise.resolve((fn)()) 同步降级 + 诊断。
/// ④ 表达式体 async 解包 completedFuture 后发射 `return X;`。
/// 已知限制（登记计划）：非 async 方法的 `.join()`（Task.Result 阻塞语义）无 TS 对应
/// 不改写；async lambda 随 ③ 同步降级。全部用例经 L3。
/// </summary>
public class TsAsyncAwaitTests : TsConversionTestBase
{
    [Fact]
    public void AsyncDelay_Emits_Await_NewPromise()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> F()
    {
        await Task.Delay(1);
        return 42;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public async f(): Promise<number> {", result.GeneratedCode);
        Assert.Contains("await new Promise<void>(resolve => setTimeout(resolve, 1));", result.GeneratedCode);
        Assert.Contains("return 42;", result.GeneratedCode);
        Assert.DoesNotContain("CompletableFuture", result.GeneratedCode);
        Assert.DoesNotContain(".join(", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void TaskYield_Emits_Await_PromiseResolve()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task G()
    {
        await Task.Yield();
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public async g(): Promise<void> {", result.GeneratedCode);
        Assert.Contains("await Promise.resolve();", result.GeneratedCode);
        Assert.Contains("return;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void WhenAll_Emits_PromiseAll()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> J(Task<int> a, Task<int> b)
    {
        await Task.WhenAll(a, b);
        return 0;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public async j(a: Promise<number>, b: Promise<number>): Promise<number> {", result.GeneratedCode);
        Assert.Contains("await Promise.all([a, b]);", result.GeneratedCode);
        Assert.DoesNotContain("CompletableFuture", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void TaskFromResult_NonAsync_Emits_PromiseResolve()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public Task<int> H()
    {
        return Task.FromResult(1);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public h(): Promise<number> {", result.GeneratedCode);
        Assert.DoesNotContain("async h", result.GeneratedCode);
        Assert.Contains("return Promise.resolve(1);", result.GeneratedCode);
        Assert.DoesNotContain("CompletableFuture", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void AwaitMethodCall_Emits_Await_On_Structured_Initializer()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> F()
    {
        return 42;
    }
    public async Task<int> I()
    {
        int a = await F();
        return a + 1;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("let a: number = await this.f();", result.GeneratedCode);
        Assert.DoesNotContain(".join(", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Await_In_If_Condition_Emits_Await()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> F()
    {
        return 42;
    }
    public async Task<int> M()
    {
        if (await F() > 0) return 1;
        return 0;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (await this.f() > 0) {", result.GeneratedCode);
        Assert.DoesNotContain(".join(", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void TaskRun_Degrades_To_Synchronous_Call_With_Diagnostic()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> K()
    {
        return await Task.Run(() => 42);
    }
    public Task<int> N() => Task.Run(() => 5);
}
""");
        Assert.True(result.Success);
        Assert.Contains("return await Promise.resolve((() => 42)());", result.GeneratedCode);
        Assert.Contains("return Promise.resolve((() => 5)());", result.GeneratedCode);
        Assert.DoesNotContain("CompletableFuture", result.GeneratedCode);
        AssertDiagnostic(result, "Task.Run");
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void ExpressionBodied_Async_Emits_Plain_Return()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<int> L() => 7;
}
""");
        Assert.True(result.Success);
        Assert.Contains("public async l(): Promise<number> {", result.GeneratedCode);
        Assert.Contains("return 7;", result.GeneratedCode);
        Assert.DoesNotContain("Promise.resolve(7)", result.GeneratedCode);
        Assert.DoesNotContain("CompletableFuture", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void ExpressionBodied_NonAsync_Task_Emits_PromiseResolve()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public Task<int> N() => Task.FromResult(9);
}
""");
        Assert.True(result.Success);
        Assert.Contains("public n(): Promise<number> {", result.GeneratedCode);
        Assert.Contains("return Promise.resolve(9);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void StringJoin_Not_Rewritten_To_Await()
    {
        var result = Convert("""
using System.Threading.Tasks;
public class Use
{
    public async Task<string> S(string[] parts)
    {
        string s = string.Join(",", parts);
        return s;
    }
}
""");
        Assert.True(result.Success);
        // 字符串联接（Task 36 成员映射 → CsString.join）保持原样，不得被 await 化
        Assert.Contains("CsString.join(\",\", parts);", result.GeneratedCode);
        Assert.DoesNotContain("await CsString.join", result.GeneratedCode);
        Assert.DoesNotContain("await StringHelper.join", result.GeneratedCode);
        Assert.DoesNotContain("await StringHelper", result.GeneratedCode);
        // 注：StringHelper.join → CsString.join 映射由 Task 36 成员映射完成（text/ 组件 Task 31 已实现），此处不验 L3
    }
}
