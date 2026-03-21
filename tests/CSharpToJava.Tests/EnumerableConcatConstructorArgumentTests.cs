using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumerableConcatConstructorArgumentTests
{
    [Fact]
    public void EnumerableConcat_InConstructorArgument_IsMaterializedFromStream()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class G<T>
            {
                public G(IEnumerable<T> edges, int n) { }
                public IEnumerable<T> Edges => new List<T>();
                public int NodeCount => 0;
            }

            public class C
            {
                G<int> graph = new G<int>(new List<int>(), 0);
                IEnumerable<int> constrained = new List<int>();

                G<int> M()
                {
                    return new G<int>((from e in graph.Edges select e).Concat(constrained), graph.NodeCount);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Stream.concat", result.GeneratedCode);
        Assert.Contains(".collect(", result.GeneratedCode);
    }
}
