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
