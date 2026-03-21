using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class QualifiedTypeNameCollisionTests
{
    [Fact]
    public void FullyQualifiedGenericType_IsPreserved_WhenCurrentNamespaceHasSameSimpleName()
    {
        const string code = """
            using System.Collections.Generic;

            namespace Demo.Core.Layout {
                public class Edge {}
            }

            namespace Demo.Graph {
                public class Edge {}

                public class Tiling {
                    public Dictionary<Demo.Core.Layout.Edge, List<int>> PathList = new Dictionary<Demo.Core.Layout.Edge, List<int>>();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("HashMap<Demo.Core.Layout.Edge, ArrayList<Integer>> PathList", result.GeneratedCode);
    }
}
