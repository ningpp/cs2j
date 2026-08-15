using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces XmlDownloadManager.GetStreamAsync:
/// `Task.Run&lt;Stream&gt;(() => new FileStream(...))` must map to
/// CompletableFuture.supplyAsync(Supplier&lt;T&gt;), not to runAsync(Runnable).
/// The converter was adding a (Runnable) cast to the value-returning lambda.
/// </summary>
public class TaskRunGenericMappingTests
{
    [Fact]
    public void TaskRunWithValueReturningLambda_UsesSupplyAsyncWithoutRunnableCast()
    {
        var result = Convert("""
using System.Threading.Tasks;

class C
{
    Task<int> M() => Task.Run(() => 42);
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("supplyAsync", code, StringComparison.Ordinal);
        Assert.Contains("() -> 42", code, StringComparison.Ordinal);
        Assert.DoesNotContain("runAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(Runnable)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskRunWithTaskReturningLambda_UsesRunAsyncWithRunnableCast()
    {
        var result = Convert("""
using System.Threading.Tasks;

class C
{
    Task M() => Task.Run(() => HelperAsync());
    static async Task HelperAsync() { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CompletableFuture.runAsync((Runnable)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Supplier<", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
