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

    [Fact]
    public async Task SelectMany_BlockLambdaReturningList_ConvertsToStreamForFlatMap()
    {
        // Mirrors XCoordsRoutingV2Test: Enumerable.Range(0, 6).SelectMany(c => {
        //     var edges = new List<...>();
        //     edges.Add(...);
        //     return edges;
        // })
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System.Collections.Generic;
                using System.Linq;

                namespace Demo
                {
                    public class Test
                    {
                        public (int, int)[] GetEdges()
                        {
                            return Enumerable.Range(0, 6)
                                .SelectMany(c => {
                                    var edges = new List<(int, int)>();
                                    if (c + 1 < 6)
                                        edges.Add((c, c + 1));
                                    return edges;
                                })
                                .ToArray();
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        // The flatMap call must not return a bare CSharpList/Collection.
        // It should use StreamSupport or similar to convert Iterable to Stream.
        Assert.Contains("StreamSupport", test.GeneratedCode);
        Assert.Contains(".andThen(", test.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
