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
    public void AsyncMethodReturningTask_Converts()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task DoWork() { await System.Threading.Tasks.Task.Delay(10); } }");
        AssertConversion(result, "public CompletableFuture doWork() {");
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
    public void CompletableFutureImportPresent()
    {
        var result = Convert("class C { public async System.Threading.Tasks.Task<int> M() { return await System.Threading.Tasks.Task.FromResult(1); } }");
        AssertJavaContains(result, "import java.util.concurrent.CompletableFuture;");
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
    public void TaskFactoryStartNew_Converts()
    {
        var result = Convert("class C { public int M() { return System.Threading.Tasks.Task.Factory.StartNew(() => 3).Result; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void TaskContinueWith_Converts()
    {
        var result = Convert("class C { public System.Threading.Tasks.Task<int> M() { return System.Threading.Tasks.Task.FromResult(1).ContinueWith(t => t.Result + 1); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }
}
