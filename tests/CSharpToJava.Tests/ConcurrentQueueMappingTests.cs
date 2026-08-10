using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Collections.Concurrent.ConcurrentQueue&lt;T&gt; maps to the
/// compat CSharpQueue (which provides enqueue/dequeue/tryDequeue). The previous
/// mapping to java.util.concurrent.ConcurrentLinkedQueue produced invalid Java
/// at call sites (enqueue does not exist on ConcurrentLinkedQueue) — reproduced
/// by ConcurrentQueueOfTConverter.Add.
/// </summary>
public class ConcurrentQueueMappingTests
{
    [Fact]
    public void ConcurrentQueue_MapsToCompatCSharpQueue()
    {
        var result = Convert(@"
class Holder
{
    public void M()
    {
        var q = new System.Collections.Concurrent.ConcurrentQueue<int>();
        q.Enqueue(5);
        q.TryDequeue(out int value);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CSharpQueue<Integer>", code);
        Assert.Contains("q.enqueue(5)", code);
        Assert.DoesNotContain("ConcurrentLinkedQueue", code);
    }

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
}
