using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that arrays produced by LINQ operations (e.g. Enumerable.Reverse().ToArray())
/// are properly wrapped when passed to CollectionAssert.AreEqual, which expects
/// Iterable&lt;?&gt; in Java. The key issue: the transformed expression starts with
/// "Arrays.stream(" but ends with ".toArray()", producing an array, not an Iterable.
/// </summary>
public class CollectionAssertArrayArgTests
{
    private readonly ITestOutputHelper _out;

    public CollectionAssertArrayArgTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task CollectionAssertAreEqual_LinqToArrayArg_WrapsArray()
    {
        // Mirrors ShortestPartRouterForLgTest.AssertPathUsesEdges:
        //   CollectionAssert.AreEqual(expectedSourceToTarget, Enumerable.Reverse(route).ToArray());
        // The second argument is a LINQ expression that returns Edge[].
        // The Java expression starts with "Arrays.stream(" but ends with ".toArray()".
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Edge.cs", Content = """
                namespace Demo
                {
                    public class Edge
                    {
                        public int Source;
                        public int Target;
                        public Edge(int s, int t) { Source = s; Target = t; }
                    }
                }
                """ },
            new SourceFile { FilePath = "Test.cs", Content = """
                using System.Collections.Generic;
                using System.Linq;
                using Microsoft.VisualStudio.TestTools.UnitTesting;

                namespace Demo
                {
                    public class Test
                    {
                        public static void AssertPathUsesEdges(Edge[] route, params Edge[] expectedSourceToTarget)
                        {
                            CollectionAssert.AreEqual(expectedSourceToTarget, Enumerable.Reverse(route).ToArray());
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        // Both arguments must be wrapped as Iterable for CollectionAssert.areEqual.
        // The first arg (expectedSourceToTarget) should be wrapped via ArrayHelper.toList.
        Assert.Contains("ArrayHelper.toList(expectedSourceToTarget)", test.GeneratedCode);
        // The second arg (LINQ .ToArray()) should NOT be left as a bare array.
        // It should be wrapped via ArrayHelper.toList or similar.
        Assert.DoesNotContain("CollectionAssert.areEqual(ArrayHelper.toList(expectedSourceToTarget), Arrays.stream", test.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
