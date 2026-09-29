using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Class&lt;T&gt; tokens are injected at call sites when the generic
/// method is declared in a different project (referenced assembly) and the
/// method does NOT directly create T[] arrays — the Class&lt;T&gt; requirement
/// comes from the runtime-class-parameter detection chain (e.g. the type
/// parameter is used in a T[][] field of a referenced class).
///
/// This mirrors the real TopologicalSort.GetOrder&lt;TEdge&gt;(BasicGraphOnEdges&lt;TEdge&gt;)
/// scenario where:
///   - BasicGraphOnEdges&lt;TEdge&gt; has TEdge[][] fields (triggers Class&lt;TEdge&gt;)
///   - GetOrder&lt;TEdge&gt; does NOT create TEdge[] directly, but still gets
///     a Class&lt;TEdge&gt; parameter through the forwarded-requirement chain
///   - The call site TopologicalSort.GetOrder(graph) must insert TestEdge.class
/// </summary>
public class InferredGenericCallSiteNoDirectArrayTests
{
    private readonly ITestOutputHelper _out;

    public InferredGenericCallSiteNoDirectArrayTests(ITestOutputHelper output) => _out = output;

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
