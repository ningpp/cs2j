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

    [Fact]
    public void ValueTaskConstructors_MapToCompletableFutureRepresentation()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    ValueTask<(int, int, int, bool)> FromTask(Task<(int, int, int, bool)> task)
    {
        return new ValueTask<(int, int, int, bool)>(task);
    }

    ValueTask<int> FromResult()
    {
        return new ValueTask<int>(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return task;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return CompletableFuture.completedFuture(42);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new CompletableFuture", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Async Task method with conditional early return ─────────────

    /// <summary>
    /// C# async Task methods implicitly return a completed Task when execution
    /// falls through the end of the method. When there is a conditional early
    /// return (e.g. inside an if-block) but the method body also has code after
    /// that block, a final return must still be emitted for the fall-through path.
    /// Regression test: the check "body contains any return" was not sufficient;
    /// it missed methods with conditional returns where the end is still reachable.
    /// </summary>
    [Fact]
    public void AsyncTask_ConditionalEarlyReturn_EmitsFinalReturn()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    async Task EncodeAsync(byte[] buffer, int index, int count)
    {
        if (count == 0)
        {
            return;
        }

        // more code after the conditional return — fall-through path
        int endIndex = index + count;
        while (index < endIndex)
        {
            index++;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The generated code must contain a return at the end for the fall-through path.
        // Verify that the last statement in the method is a return, not just that
        // some return exists deep inside an if-block.
        var lines = result.GeneratedCode.Replace("\r\n", "\n").Split('\n');
        // Trim trailing empty lines and closing braces
        var trimmed = lines.Reverse().SkipWhile(l => string.IsNullOrWhiteSpace(l) || l.Trim() == "}").ToArray();
        // The last substantive line should be a return
        Assert.Contains("return CompletableFuture.completedFuture(null);", trimmed.FirstOrDefault() ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// When an async Task method already has an unconditional return as its
    /// last statement, we should NOT add a duplicate (would be unreachable code).
    /// </summary>
    [Fact]
    public void AsyncTask_UnconditionalFinalReturn_DoesNotDuplicate()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    async Task DoAsync()
    {
        await Task.Delay(100);
        // no early returns; async machinery handles implicit completion
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should have exactly one return at the end
        var lines = result.GeneratedCode.Split('\n');
        var returnCount = lines.Count(l => l.Contains("return CompletableFuture.completedFuture(null);"));
        Assert.Equal(1, returnCount);
    }

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

    [Fact]
    public void AsyncTask_BreakingInfiniteLoop_StillEmitsFinalReturn()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    async Task FinishAsync(Task task, bool stop)
    {
        while (true)
        {
            await task.ConfigureAwait(false);
            if (stop)
            {
                break;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return CompletableFuture.completedFuture(null);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncTask_StateMachineTerminalThrow_DoesNotEmitUnreachableFinalReturn()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class TestClass
{
    async Task FinishAsync(bool more)
    {
        if (more)
        {
            await Task.Delay(1);
            goto Done;
        }

        await Task.Delay(2);

    Done:
        more = false;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("throw new IllegalStateException(\"Unexpected state\");", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"(?s)throw\s+new\s+IllegalStateException\(""Unexpected state""\);\s*return\s+CompletableFuture\.completedFuture\(null\);",
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
