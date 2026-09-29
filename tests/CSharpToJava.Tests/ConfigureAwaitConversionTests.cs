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

    /// <summary>
    /// When an async Task method already has an unconditional return as its
    /// last statement, we should NOT add a duplicate (would be unreachable code).
    /// </summary>

    [Fact]
    public void AsyncTask_NonBreakingInfiniteLoop_DoesNotEmitUnreachableFinalReturn()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    enum NextFunc
    {
        Again,
        Done
    }

    NextFunc _next;

    async Task FinishAsync(Task task)
    {
        while (true)
        {
            await task.ConfigureAwait(false);
            switch (_next)
            {
                case NextFunc.Again:
                    task = Task.CompletedTask;
                    break;
                case NextFunc.Done:
                    return;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        Assert.DoesNotMatch(
            @"(?s)while\s*\(true\).*?\}\s*return\s+CompletableFuture\.completedFuture\(null\);",
            result.GeneratedCode);
    }

    /// <summary>
    /// An async Task method whose body ends in a switch statement that returns
    /// from every section (including default) cannot fall through. Adding a
    /// final return would generate unreachable code in Java.
    /// </summary>
    [Fact]
    public void AsyncTask_ExhaustiveSwitch_DoesNotEmitUnreachableFinalReturn()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    async Task ParseHowManyAsync(int token)
    {
        switch (token)
        {
            case 1:
                return;
            case 2:
                return;
            case 3:
                return;
            default:
                return;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The switch already returns on all paths; no trailing return should be emitted.
        Assert.DoesNotMatch(
            @"(?s)switch\s*\(.*token.*\)\s*\{.*?\}\s*return\s+CompletableFuture\.completedFuture\(null\);",
            result.GeneratedCode);
    }
}
