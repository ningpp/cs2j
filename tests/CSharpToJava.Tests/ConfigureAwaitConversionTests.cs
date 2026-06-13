using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for ConfigureAwait conversion.
/// C# Task.ConfigureAwait(bool) controls synchronization context capture;
/// Java has no equivalent, so the call should be stripped entirely.
/// e.g. task.ConfigureAwait(false) → task
/// </summary>
public class ConfigureAwaitConversionTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ConfigureAwait_False_IsStripped()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    async Task TestAsync()
    {
        await Task.Delay(500).ConfigureAwait(false);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ConfigureAwait", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".configureAwait(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureAwait_True_IsStripped()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    async Task TestAsync()
    {
        await Task.Delay(500).ConfigureAwait(true);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ConfigureAwait", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".configureAwait(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureAwait_WithVariable_IsStripped()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    static async Task TestAsync(bool configureAwait)
    {
        await Task.Delay(500).ConfigureAwait(configureAwait);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ConfigureAwait", result.GeneratedCode, StringComparison.Ordinal);
        // The parameter name "configureAwait" may appear in the method signature,
        // but there should be no method call like ".configureAwait("
        Assert.DoesNotContain(".configureAwait(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureAwait_OnGenericTask_IsStripped()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    async Task<int> TestAsync()
    {
        return await SomeAsync().ConfigureAwait(false);
    }

    Task<int> SomeAsync() => Task.FromResult(42);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ConfigureAwait", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".configureAwait(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureAwait_ProducesValidCompletableFuture()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    static async Task TestAsync(bool flag)
    {
        await Task.Delay(500).ConfigureAwait(flag);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The result should contain CompletableFuture (from async method return type)
        Assert.Contains("CompletableFuture", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain configureAwait method call on CompletableFuture
        Assert.DoesNotContain(".configureAwait(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Task.CompletedTask tests ────────────────────────────────────

    [Fact]
    public void TaskCompletedTask_MapsToCompletedFutureNull()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    Task DoAsync()
    {
        return Task.CompletedTask;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("completedFuture(null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getCompletedTask", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletedTask", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Task.FromResult tests ───────────────────────────────────────

    [Fact]
    public void TaskFromResult_MapsToCompletedFuture()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    Task<int> GetResultAsync()
    {
        return Task.FromResult(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("completedFuture", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FromResult", result.GeneratedCode, StringComparison.Ordinal);
    }
}
