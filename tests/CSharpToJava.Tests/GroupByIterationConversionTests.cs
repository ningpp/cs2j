using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for iterating/streaming an IGrouping. C# IGrouping&lt;K,V&gt;
/// maps to a Java Map.Entry whose runtime object is CSharpKeyValuePair, which is
/// not Iterable: the element-typed (Iterable&lt;T&gt;)(Iterable) re-type cast
/// throws ClassCastException, so the converter must adapt through
/// CSharpGenericIterable.fromAnyTyped (or .getValue()).
/// </summary>
public class GroupByIterationConversionTests
{
    private const string GroupingSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public class Node
        {
            public double Y;
            public Node(double y) { Y = y; }
        }

        public static class GroupingSample
        {
            public static List<int[]> Layers(List<Node> nodes)
            {
                var list = new List<int>();
                for (int i = 0; i < nodes.Count; i++)
                    list.Add(i);

                var layers = list.GroupBy(i => nodes[i].Y);
                var layerList = new List<int[]>();
                foreach (var layer in layers)
                {
                    int total = 0;
                    foreach (int item in layer)
                        total += item;
                    layerList.Add(layer.ToArray());
                }
                return layerList;
            }
        }
        """;

    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Grouping.cs", Content = source } },
                projectName: "GroupingDemo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task Grouping_ToArray_DoesNotCastGroupingToIterable()
    {
        var code = await ConvertAsync(GroupingSource);

        // The IGrouping variable is a Map.Entry/CSharpKeyValuePair at runtime;
        // re-typing it to Iterable throws ClassCastException.
        Assert.DoesNotContain("(Iterable<Integer>)(Iterable)", code);
        Assert.Contains("CSharpGenericIterable.<Integer>fromAnyTyped(layer)", code);
    }

    [Fact]
    public async Task Grouping_Foreach_DoesNotCastGroupingToIterable()
    {
        var code = await ConvertAsync(GroupingSource);

        // The nested foreach over the grouping must adapt the entry to its value
        // list instead of emitting the element-typed Iterable cast.
        Assert.Contains("for (int item : CSharpGenericIterable.<Integer>fromAnyTyped(layer))", code);
        Assert.DoesNotContain("for (int item : (Iterable<Integer>)(Iterable)", code);
    }
}
