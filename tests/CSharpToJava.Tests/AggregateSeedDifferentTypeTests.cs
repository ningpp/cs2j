using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class AggregateSeedDifferentTypeTests
{
    [Fact]
    public void Aggregate_WithDifferentSeedType_UsesThreeArgReduce()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            class C {
                class Node {
                    public Node Next;
                    public int V;
                }

                Node Build(IEnumerable<int> values) {
                    var head = new Node();
                    values.Aggregate(head, (n, v) => n.Next = new Node { V = v });
                    return head;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(".reduce(head,", result.GeneratedCode);
        Assert.Contains("(__accLeft, __accRight) -> __accRight", result.GeneratedCode);
    }
}
