using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Class&lt;T&gt; tokens are injected at call sites when the generic
/// method is declared in a different project (referenced assembly). The
/// RuntimeClassParameterRegistry (on ConversionOptions, shared across projects)
/// carries the method's Class-token requirements from the declaring project's
/// ConversionContext to the calling project's ConversionContext.
/// </summary>
public class CrossProjectClassTokenTests
{
    private readonly ITestOutputHelper _out;

    public CrossProjectClassTokenTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task CrossProject_GenericMethodCall_InjectsClassTokenFromSharedRegistry()
    {
        // Simulates the TopologicalSort.GetOrder<TEdge>(BasicGraphOnEdges<TEdge> graph)
        // scenario: the method is declared in the "core" project and called from
        // the "test" project. The core project registers the method's Class<TEdge>
        // requirement in the shared RuntimeClassParameterRegistry. When the test
        // project is converted, it finds the requirement from the registry and
        // injects TestEdge.class at the call site.
        var options = new ConversionOptions();

        // Step 1: Convert the "core" project (declares the generic method).
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
                    public int Count;
                    public Graph(int count) { Count = count; }
                    public Graph() { }
                    public IEnumerable<TEdge> OutEdges(int u) { return new TEdge[0]; }
                }

                public static class TopologicalSort
                {
                    // The method uses TEdge in an array creation (new TEdge[0]),
                    // which triggers the RuntimeClassParameterHelper to register
                    // the Class<TEdge> requirement in the shared registry.
                    public static int[] GetOrder<TEdge>(Graph<TEdge> graph) where TEdge : IEdge
                    {
                        var arr = new TEdge[0];
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
        // The method must have a Class<TEdge> parameter (proves registration happened).
        Assert.Contains("Class<TEdge>", coreSortFile.GeneratedCode);
        _out.WriteLine("Core TopologicalSort.java:");
        _out.WriteLine(coreSortFile.GeneratedCode ?? "FAILED");

        // Step 2: Compile the core source into a DLL so the test project can
        // reference it as a metadata assembly (simulating a project reference).
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
                        var graph = new Graph<TestEdge>(4);
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
}
