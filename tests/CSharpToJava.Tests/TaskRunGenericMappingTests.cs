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
