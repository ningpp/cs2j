using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that generic method calls where the type argument is inferred from
/// the argument (not explicitly specified) correctly insert the Class<T> token
/// in the Java output. This mirrors the TopologicalSort.GetOrder(graph) case
/// where C# infers TEdge from the BasicGraphOnEdges<TEdge> parameter.
/// </summary>
public class GenericMethodInferredTypeArgClassTokenTests
{
    private readonly ITestOutputHelper _out;

    public GenericMethodInferredTypeArgClassTokenTests(ITestOutputHelper output) => _out = output;

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
