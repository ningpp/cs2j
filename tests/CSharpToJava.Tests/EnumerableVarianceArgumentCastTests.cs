using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumerableVarianceArgumentCastTests
{
    public interface IEdge { }
    public class IntPair : IEdge { }

    [Fact]
    public void SetOfDerived_PassedToIEnumerableOfBase_InsertsIterableBridgeCast()
    {
        const string code = """
            using System.Collections.Generic;

            public interface IEdge { }
            public class IntPair : IEdge { }

            public class Graph {
                public Graph(IEnumerable<IEdge> edges) { }
            }

            public class C {
                public void M(HashSet<IntPair> pairs) {
                    var g = new Graph(pairs);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(Iterable<IEdge>)(Iterable<?>)(pairs)", result.GeneratedCode.Replace(" ", string.Empty));
    }
}
