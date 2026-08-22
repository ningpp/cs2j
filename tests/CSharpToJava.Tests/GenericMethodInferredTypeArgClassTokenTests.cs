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

    [Fact]
    public void GenericMethodCall_InferredTypeArg_InsertsClassToken()
    {
        // Mirrors TopologicalSort.GetOrder<TEdge>(BasicGraphOnEdges<TEdge> graph):
        //   - The method has a Class<TEdge> parameter added during conversion
        //     because it forwards TEdge to a method that needs the Class token.
        //   - The call site GetOrder(graph) infers TEdge from the argument type.
        //   - The Java output must include TestEdge.class at the call site.
        var result = Convert("""
using System.Collections.Generic;

interface IEdge
{
    int Source { get; }
    int Target { get; }
}

class TestEdge : IEdge
{
    public int Source { get; }
    public int Target { get; }
    public TestEdge(int s, int t) { Source = s; Target = t; }
}

class Graph<T> where T : IEdge
{
    public int Count;
    public Graph(int count) { Count = count; }
    public Graph() {}
}

static class TopologicalSort
{
    public static int[] GetOrder<TEdge>(Graph<TEdge> graph) where TEdge : IEdge
    {
        var arr = new TEdge[0];
        return new int[0];
    }
}

class Test
{
    static void Run()
    {
        var graph = new Graph<TestEdge>(4);
        var order = TopologicalSort.GetOrder(graph);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The call site must include the Class<T> token (.class literal).
        Assert.Contains("TestEdge.class", code, StringComparison.Ordinal);
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
