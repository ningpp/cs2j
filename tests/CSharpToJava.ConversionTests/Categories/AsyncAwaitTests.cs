using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# async/await to Java CompletableFuture.
/// </summary>
public class AsyncAwaitTests : ConversionTestBase
{
    [Fact]
    public void AsyncTaskReturn_ConvertsToCompletableFuture()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { return 1; } }");
        AssertConversion(result, "import java.util.concurrent.CompletableFuture;", "public CompletableFuture<Integer> m() {");
        AssertJavaDoesNotContain(result, "async", "C# 'async' must be removed");
        AssertJavaDoesNotContain(result, "await", "C# 'await' must be removed");
    }

    [Fact]
    public void AwaitTaskDelay_ConvertsToDelayedExecutor()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(1); } }");
        AssertConversion(result, "CompletableFuture.delayedExecutor(1).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AwaitTaskFromResult_ConvertsToCompletedFuture()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { int v = await System.Threading.Tasks.Task.FromResult(5); return v; } }");
        AssertConversion(result, "CompletableFuture.completedFuture(5).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskRun_ConvertsToSupplyAsync()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { return await System.Threading.Tasks.Task.Run(() => 42); } }");
        AssertConversion(result, "CompletableFuture.supplyAsync((Supplier<Integer>) () -> 42)");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskWhenAll_ConvertsToAllOf()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.WhenAll(System.Threading.Tasks.Task.Delay(1)); } }");
        AssertConversion(result, "CompletableFuture.allOf(");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskFromResult_ConvertsToCompletedFuture()
    {
        var result = Convert("class C { public System.Threading.Tasks.Task<int> M() { return System.Threading.Tasks.Task.FromResult(5); } }");
        AssertConversion(result, "return CompletableFuture.completedFuture(5);");
    }

    [Fact]
    public void TaskCompletedTask_ConvertsToCompletedFutureNull()
    {
        var result = Convert("class C { public System.Threading.Tasks.Task M() { return System.Threading.Tasks.Task.CompletedTask; } }");
        AssertConversion(result, "return CompletableFuture.completedFuture(null);");
    }

    [Fact]
    public void AsyncMethodReturningTask_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(10); } }");
        AssertConversion(result, "public CompletableFuture doWork() {");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void AwaitInsideTry_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { try { return await System.Threading.Tasks.Task.FromResult(1); } catch (System.Exception e) { return 0; } } }");
        AssertConversion(result, "CompletableFuture.completedFuture(1).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskReturningGenericList_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<System.Collections.Generic.List<int>> M() { return new System.Collections.Generic.List<int>(); } }");
        AssertConversion(result, "public CompletableFuture<CSharpList<Integer>> m() {");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void ConfigureAwait_Ignored()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { var t = System.Threading.Tasks.Task.FromResult(1); return await t.ConfigureAwait(false); } }");
        AssertConversion(result, "CompletableFuture");
        AssertJavaDoesNotContain(result, "ConfigureAwait", "ConfigureAwait must be removed");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AsyncWithMultipleAwaits_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { int a = await System.Threading.Tasks.Task.FromResult(1); int b = await System.Threading.Tasks.Task.FromResult(2); return a + b; } }");
        AssertConversion(result, "CompletableFuture.completedFuture(1).join()", "CompletableFuture.completedFuture(2).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskRunWithArgument_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M(int n) { return await System.Threading.Tasks.Task.Run(() => n * 2); } }");
        AssertConversion(result, "CompletableFuture.supplyAsync((Supplier<Integer>) () -> n * 2)");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void TaskDelayZero_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Delay(0); } }");
        AssertConversion(result, "CompletableFuture.delayedExecutor(0).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AsyncReturnsConstant_ConvertsToCompletedFuture()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<string> M() { return \"done\"; } }");
        AssertConversion(result, "return CompletableFuture.completedFuture(\"done\");");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void TaskWhenAny_ConvertsToAnyOf()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.Delay(1)); } }");
        AssertConversion(result, "CompletableFuture.anyOf(");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AwaitInsideLoop_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { int s = 0; for (int i = 0; i < 3; i++) { s += await System.Threading.Tasks.Task.FromResult(i); } return s; } }");
        AssertConversion(result, "CompletableFuture.completedFuture(i).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AsyncMethodCallsAnotherAsync_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> A() { return await B(); } public async System.Threading.Tasks.Task<int> B() { return await System.Threading.Tasks.Task.FromResult(1); } }");
        AssertConversion(result, "CompletableFuture.completedFuture(1).join()");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void TaskYield_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.Yield(); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void RunSynchronouslyLikePattern_Converts()
    {
        var result = Convert("class C { public int M() { return System.Threading.Tasks.Task.Run(() => 7).Result; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, ".Result", "'Result' property may need mapping");
    }

    [Fact]
    public void AsyncVoidMethod_Converts()
    {
        var result = Convert("class C { public async void M() { await System.Threading.Tasks.Task.Delay(1); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void TaskFromResultChainedAwait_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<bool> M() { var ok = await System.Threading.Tasks.Task.FromResult(true); return ok; } }");
        AssertConversion(result, "CompletableFuture.completedFuture(true).join()");
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void CompletableFutureImportPresent()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { return await System.Threading.Tasks.Task.FromResult(1); } }");
        AssertJavaContains(result, "import java.util.concurrent.CompletableFuture;");
    }

    [Fact]
    public void AsyncReturningDouble_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<double> M() { return 1.5; } }");
        AssertConversion(result, "public CompletableFuture<Double> m() {", "return CompletableFuture.completedFuture(1.5);");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void AsyncReturningBool_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<bool> M() { return true; } }");
        AssertConversion(result, "public CompletableFuture<Boolean> m() {", "return CompletableFuture.completedFuture(true);");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void AwaitTaskRunSideEffect_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task M(System.Collections.Generic.List<int> l) { await System.Threading.Tasks.Task.Run(() => l.Add(1)); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaDoesNotContain(result, "await");
    }

    [Fact]
    public void AsyncMethodWithParameter_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M(int x) { return await System.Threading.Tasks.Task.FromResult(x * x); } }");
        AssertConversion(result, "CompletableFuture.completedFuture(x * x).join()");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void TaskFactoryStartNew_Converts()
    {
        var result = Convert("class C { public int M() { return System.Threading.Tasks.Task.Factory.StartNew(() => 3).Result; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void AsyncNestedAwaitInConditional_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M(bool b) { if (b) return await System.Threading.Tasks.Task.FromResult(1); return await System.Threading.Tasks.Task.FromResult(2); } }");
        AssertJavaContainsAny(result, "CompletableFuture.completedFuture(1).join()", "CompletableFuture.completedFuture(2).join()");
        AssertJavaDoesNotContain(result, "async", "await");
    }

    [Fact]
    public void TaskContinueWith_Converts()
    {
        var result = Convert("class C { public System.Threading.Tasks.Task<int> M() { return System.Threading.Tasks.Task.FromResult(1).ContinueWith(t => t.Result + 1); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void AsyncReturningCustomType_Converts()
    {
        var result = Convert("class Item { } class C { public async System.Threading.Tasks.Task<Item> M() { return new Item(); } }");
        AssertConversion(result, "public CompletableFuture<Item> m() {", "return CompletableFuture.completedFuture(new Item());");
        AssertJavaDoesNotContain(result, "async", "await");
    }
}
