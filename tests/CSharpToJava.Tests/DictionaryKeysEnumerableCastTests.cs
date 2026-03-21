using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DictionaryKeysEnumerableCastTests
{
    public interface IEdge { }
    public class IntPair : IEdge { }

    [Fact]
    public void DictionaryKeys_ToIEnumerableBase_InsertsIterableBridgeCast()
    {
        const string code = """
            using System.Collections.Generic;

            public interface IEdge { }
            public class IntPair : IEdge { }

            public class Graph {
                public Graph(IEnumerable<IEdge> edges) { }
            }

            public class C {
                public void M(Dictionary<IntPair, int> pairs) {
                    var g = new Graph(pairs.Keys);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(Iterable<IEdge>)(Iterable<?>)", result.GeneratedCode.Replace(" ", string.Empty));
    }
}
