using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NestedObjectInitializerSetterSkipTests
{
    [Fact]
    public void NestedObjectInitializer_DoesNotEmitInvalidZeroArgSetterCall()
    {
        const string code = """
            public class OverlapRemovalSettings {
                public double NodeSeparation { get; set; }
            }

            public class ProximityOverlapRemoval {
                public OverlapRemovalSettings Settings { get; set; } = new OverlapRemovalSettings();
                public ProximityOverlapRemoval(object g) { }

                public static void RemoveOverlaps(object graph, double nodeSeparation) {
                    var prism = new ProximityOverlapRemoval(graph) { Settings = { NodeSeparation = nodeSeparation } };
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("setSettings(/* TODO: ObjectInitializerExpression", result.GeneratedCode);
    }
}
