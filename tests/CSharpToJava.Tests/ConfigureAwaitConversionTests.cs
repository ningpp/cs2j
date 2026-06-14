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

    [Fact]
    public void ValueTaskCompletionMembers_MapToCompletableFutureMembers()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    bool IsReady(ValueTask<(int, int, int, bool)> task)
    {
        if (!task.IsCompletedSuccessfully)
            return false;
        return task.Result.Item1 == 42;
    }

    Task<(int, int, int, bool)> AsTask(ValueTask<(int, int, int, bool)> task)
    {
        return task.AsTask();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("!task.isDone()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("task.join()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return task;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getIsCompletedSuccessfully", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getResult", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".asTask(", result.GeneratedCode, StringComparison.Ordinal);
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
}
