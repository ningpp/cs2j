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

    [Fact]
    public async Task CrossProject_GenericMethodCall_NoDirectArrayCreation_InjectsClassToken()
    {
        // Simulates the real TopologicalSort.GetOrder<TEdge> scenario:
        // - Graph<TEdge> has TEdge[][] fields (triggers Class<TEdge> requirement)
        // - GetOrder<TEdge> does NOT create TEdge[] directly
        // - Call site must still inject TestEdge.class in the test project
        var options = new ConversionOptions();

        var coreSource = new SourceFile { FilePath = "CoreLib.cs", Content = """
            using System.Collections.Generic;

            namespace Demo.Core
            {
                public interface IEdge
                {
                    int Source { get; }
                    int Target { get; }
                }

                public class Graph<TEdge> where TEdge : IEdge
                {
                    TEdge[][] outEdges;
                    int count;

                    public Graph(IEnumerable<TEdge> edges, int numberOfVerts)
                    {
                        count = numberOfVerts;
                        outEdges = new TEdge[numberOfVerts][];
                        for (int i = 0; i < numberOfVerts; i++)
                            outEdges[i] = new TEdge[0];
                    }

                    public int NodeCount { get { return count; } }
                    public IList<TEdge> OutEdges(int vertex) { return outEdges[vertex]; }
                }

                public static class TopologicalSort
                {
                    // GetOrder<TEdge> does NOT create TEdge[] directly,
                    // but receives a Graph<TEdge> that needs Class<TEdge>.
                    public static int[] GetOrder<TEdge>(Graph<TEdge> graph) where TEdge : IEdge
                    {
                        var visited = new bool[graph.NodeCount];
                        var order = new List<int>();
                        for (int u = 0; u < graph.NodeCount; u++)
                        {
                            if (visited[u]) continue;
                            visited[u] = true;
                            foreach (var e in graph.OutEdges(u))
                            {
                                if (!visited[e.Target])
                                    visited[e.Target] = true;
                            }
                            order.Add(u);
                        }
                        return new int[0];
                    }
                }
            }
            """ };

        var corePipeline = new ProjectConversionPipeline(options);
        var coreResults = await corePipeline.ConvertProjectAsync(
            new[] { coreSource },
            projectName: "CoreLib");

        var coreSortFile = Assert.Single(coreResults, r => r.FileName == "TopologicalSort.java");
        Assert.True(coreSortFile.Success, "CORE:\n" + coreSortFile.GeneratedCode + "\n" + string.Join("\n", coreSortFile.Diagnostics));
        _out.WriteLine("Core TopologicalSort.java:");
        _out.WriteLine(coreSortFile.GeneratedCode ?? "FAILED");

        // Step 2: Compile the core source into a DLL so the test project can reference it.
        var coreCompilation = CSharpCompilation.Create(
            "CoreLib",
            new[] { CSharpSyntaxTree.ParseText(coreSource.Content, path: coreSource.FilePath) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var coreStream = new System.IO.MemoryStream();
        var emitResult = coreCompilation.Emit(coreStream);
        Assert.True(emitResult.Success,
            "Core emit failed: " + string.Join(", ", emitResult.Diagnostics));
        coreStream.Position = 0;

        var coreReference = MetadataReference.CreateFromStream(coreStream);

        // Step 3: Compile the test source with the core DLL as a reference.
        var testSourceContent = """
            using Demo.Core;

            namespace Demo.Tests
            {
                public class TestEdge : IEdge
                {
                    public int Source { get; }
                    public int Target { get; }
                    public TestEdge(int s, int t) { Source = s; Target = t; }
                }

                public class Test
                {
                    public void Run()
                    {
                        var edges = new System.Collections.Generic.List<TestEdge>();
                        var graph = new Graph<TestEdge>(edges, 4);
                        var order = TopologicalSort.GetOrder(graph);
                    }
                }
            }
            """;

        var testTree = CSharpSyntaxTree.ParseText(testSourceContent, path: "Test.cs");
        var testCompilation = CSharpCompilation.Create(
            "TestProject",
            new[] { testTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                coreReference,
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Step 4: Convert the test project using the pre-built compilation.
        var testPipeline = new ProjectConversionPipeline(options);
        var testResults = await testPipeline.ConvertProjectAsync(
            testCompilation,
            emitFilePaths: new HashSet<string> { System.IO.Path.GetFullPath("Test.cs") },
            projectName: "TestProject",
            isTestProject: true);

        var testFile = Assert.Single(testResults, r => r.FileName == "Test.java");
        Assert.True(testFile.Success, "TEST:\n" + testFile.GeneratedCode + "\n" + string.Join("\n", testResults.SelectMany(r => r.Diagnostics)));
        _out.WriteLine("Test output:");
        _out.WriteLine(testFile.GeneratedCode ?? "FAILED");
        // The call site must include the Class<T> token (.class literal).
        Assert.Contains("TestEdge.class", testFile.GeneratedCode);
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
