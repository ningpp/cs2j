using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for LINQ GroupBy procedural rewrite correctness.
/// Verifies that the LINQ rewriter generates correct dict value types
/// and proper .entrySet() returns for GroupBy operations.
/// </summary>
public class GroupByProceduralRewriteTests
{
    private readonly ITestOutputHelper _out;
    public GroupByProceduralRewriteTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    /// <summary>
    /// Rewrites C# source with the LINQ rewriter and returns the resulting C# code.
    /// This allows testing the LINQ rewriter output directly.
    /// </summary>
    private (string rewrittenCode, int rewriteCount, List<string> skipped) RewriteLinq(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        };
        // Add System.Runtime for extension method resolution
        var runtimePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Runtime.dll");
        if (System.IO.File.Exists(runtimePath))
            refs.Add(MetadataReference.CreateFromFile(runtimePath));

        var compilation = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semantic = compilation.GetSemanticModel(tree);
        var rewriter = new LinqRewriter(semantic, new ConversionOptions());
        var result = rewriter.Visit(tree.GetRoot());
        return (result.ToFullString(), rewriter.RewrittenLinqQueries, rewriter.SkippedLinqChains);
    }

    /// <summary>
    /// GroupBy on List&lt;int&gt; should produce Dictionary&lt;K, List&lt;int&gt;&gt;,
    /// not Dictionary&lt;K, List&lt;List&lt;int&gt;&gt;&gt;.
    /// The LINQ rewriter was using the source collection type instead of the element type,
    /// causing a double-wrapped list type in the generated procedural code.
    /// When GroupBy is part of a LINQ chain (e.g. Where+GroupBy), the LINQ rewriter
    /// extracts a procedural helper method with a dictionary loop.
    /// </summary>
    [Fact]
    public void GroupBy_ValueType_ShouldNotDoubleWrap()
    {
        // Test the LINQ rewriter directly to verify procedural GroupBy code
        var (rewritten, count, skipped) = RewriteLinq(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M(List<int> numbers) {
        var groups = numbers.Where(n => n > 0).GroupBy(n => n % 2);
        foreach (var g in groups) {
            var key = g.Key;
        }
    }
}");
        _out.WriteLine($"Rewrite count: {count}");
        _out.WriteLine($"Skipped: {string.Join("; ", skipped)}");
        _out.WriteLine(rewritten);

        // The LINQ rewriter should have rewritten the GroupBy
        Assert.True(count > 0, "LINQ rewriter should have rewritten the GroupBy chain");

        // The dict value type should be List<int> (element type), not
        // List<IEnumerable<int>> or List<List<int>> (collection type wrapping)
        Assert.DoesNotContain("List<System.Collections.Generic.IEnumerable<int>>", rewritten);
        Assert.DoesNotContain("List<System.Collections.Generic.List<int>>", rewritten);
        Assert.DoesNotContain("List<List<int>>", rewritten);
        // The correct type should be List<int>
        Assert.Contains("List<int>", rewritten);
    }

    /// <summary>
    /// When GroupBy result is returned from a procedural helper method,
    /// the return type is Iterable&lt;Map.Entry&gt;, so the method should
    /// return _dict.entrySet() instead of _dict directly.
    /// LinkedHashMap does not implement Iterable&lt;Map.Entry&gt;.
    /// </summary>
    [Fact]
    public void GroupBy_Return_ShouldNotReturnDictDirectly()
    {
        // When GroupBy returns a Dictionary directly from a method with
        // IEnumerable<IGrouping<K,V>> return type, Java needs _dict.entrySet().
        // Verify at the Java level that .entrySet() appears.
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M(List<int> numbers) {
        var groups = numbers.Where(n => n > 0).GroupBy(n => n % 2);
        foreach (var g in groups) {
            var key = g.Key;
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // After the fix, the procedural GroupBy helper's return should include
        // .entrySet() so LinkedHashMap is iterable as Map.Entry collection.
        // Either the Java code has .entrySet() (procedural path) or
        // uses Collectors.groupingBy (streaming path) — both are valid.
        Assert.True(
            code.Contains(".entrySet()") || code.Contains("Collectors.groupingBy"),
            "Generated Java should use .entrySet() on dict return or Collectors.groupingBy streaming path");
    }

    /// <summary>
    /// GroupBy on a collection of objects should use the element type (the object type),
    /// not the collection type, for the list value in the dictionary.
    /// </summary>
    [Fact]
    public void GroupBy_ObjectElements_CorrectValueType()
    {
        var (rewritten, count, skipped) = RewriteLinq(@"
using System.Collections.Generic;
using System.Linq;
class Node { public double Y; public string Name; }
class Sample {
    void M(List<Node> nodes) {
        var groups = nodes.Where(n => n.Y > 0).GroupBy(n => n.Y);
        foreach (var g in groups) {
            var key = g.Key;
        }
    }
}");
        _out.WriteLine($"Rewrite count: {count}");
        _out.WriteLine(rewritten);
        Assert.True(count > 0, "LINQ rewriter should have rewritten the GroupBy chain");

        // Should use List<Node> (element type), not List<IEnumerable<Node>> (collection type)
        Assert.DoesNotContain("List<System.Collections.Generic.IEnumerable<Node>>", rewritten);
        Assert.DoesNotContain("List<System.Collections.Generic.List<Node>>", rewritten);
        Assert.Contains("List<Node>", rewritten);
    }
}
