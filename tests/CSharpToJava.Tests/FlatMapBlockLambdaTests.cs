using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that SelectMany with a block-body lambda (whose return type is
/// IEnumerable&lt;T&gt;) correctly converts the lambda's result to a Stream
/// for Java's flatMap, which expects Stream&lt;T&gt; (not Iterable&lt;T&gt;).
/// </summary>
public class FlatMapBlockLambdaTests
{
    private readonly ITestOutputHelper _out;

    public FlatMapBlockLambdaTests(ITestOutputHelper output) => _out = output;

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
