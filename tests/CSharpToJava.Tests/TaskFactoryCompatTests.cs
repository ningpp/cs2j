using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Task.Factory.StartNew(...) must map to the compat Task class's static
/// getFactory() (io.github.ningpp.compat.Task), not CompletableFuture.getFactory().
/// The System.Threading.Tasks.Task type maps to CompletableFuture for async/await
/// usage, but Task.Factory is only implemented on the compat Task class.
/// Regression: GraphmapsWpfControl TileFetcher.UpdateTiles.
/// </summary>
public class TaskFactoryCompatTests
{
    private readonly ITestOutputHelper _out;

    public TaskFactoryCompatTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TaskFactoryStartNew_UsesCompatTaskGetFactory_NotCompletableFuture()
    {
        var result = Convert("""
using System.Threading;
using System.Threading.Tasks;

class Sample {
    void Update(CancellationTokenSource cts) {
        Task.Factory.StartNew(() =>
            {
                System.Console.WriteLine("work");
            },
            cts.Token,
            TaskCreationOptions.None,
            TaskScheduler.Default).ContinueWith(t =>
            {
                System.Console.WriteLine("done");
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Task.getFactory().startNew(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletableFuture.getFactory()", code, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.getDefault()", code, StringComparison.Ordinal);
        Assert.Contains("TaskCreationOptions.None", code, StringComparison.Ordinal);
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
