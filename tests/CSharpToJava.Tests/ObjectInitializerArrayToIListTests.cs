using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# array values assigned to IList&lt;T&gt; properties via object
/// initializers are properly wrapped. In C#, T[] implements IList&lt;T&gt;,
/// so "new Foo { Nodes = arrayVar }" compiles. In Java, the converted
/// setNodes(CSharpGenericIList&lt;T&gt;) does not accept T[], so the array
/// must be wrapped via ArrayHelper.toList().
/// </summary>
public class ObjectInitializerArrayToIListTests
{
    private readonly ITestOutputHelper _out;

    public ObjectInitializerArrayToIListTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ObjectInitializer_ArrayAssignedToIListProperty_IsWrapped()
    {
        // Mirrors E:\agl-master\GraphLayout\...\OrderingInternalTest.cs:
        //   var nodes = Enumerable.Range(0, 6).Select(i => new Node(...)).ToArray();
        //   var baseGraph = new BasicGraph<Node, PolyIntEdge>(edges, 6) { Nodes = nodes };
        var result = Convert("""
using System.Collections.Generic;

class Node {}

class Container
{
    public IList<Node> Nodes { get; set; }
}

class Test
{
    static void Run()
    {
        var nodes = new Node[] { new Node(), new Node() };
        var c = new Container { Nodes = nodes };
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The array must be wrapped — not passed raw to setNodes.
        // It should contain ArrayHelper.toList or CSharpList wrapping, not bare setNodes(nodes)
        Assert.Contains("setNodes(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("setNodes(nodes)", code, StringComparison.Ordinal);
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
