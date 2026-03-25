using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FastIncrementalLayoutIdealEdgeLengthRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_FastIncrementalLayoutSettings_AddsNullGuards()
    {
        const string generated = """
            package Microsoft.Msagl.Layout.Incremental;

            public class FastIncrementalLayoutSettings {
                private EdgeConstraints idealEdgeLength;

                public EdgeConstraints getIdealEdgeLength() {
                    return idealEdgeLength;
                }

                public void setIdealEdgeLength(EdgeConstraints value) {
                    this.idealEdgeLength = value;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("FastIncrementalLayoutSettings.java", generated);

        Assert.Contains("private EdgeConstraints idealEdgeLength = new EdgeConstraints();", output);
        Assert.Contains("if (idealEdgeLength == null)", output);
        Assert.Contains("this.idealEdgeLength = value != null ? value : new EdgeConstraints();", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_FastIncrementalLayout_GuardsGenerateEdgeConstraintsCall()
    {
        const string generated = """
            package Microsoft.Msagl.Layout.Incremental;

            public class FastIncrementalLayout {
                void setupConstraints() {
                    EdgeConstraintGenerator.generateEdgeConstraints(graph.getEdges(), settings.getIdealEdgeLength().clone(), horizontalSolver, verticalSolver);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("FastIncrementalLayout.java", generated);

        Assert.Contains("EdgeConstraints _ideal = settings.getIdealEdgeLength();", output);
        Assert.Contains("settings.setIdealEdgeLength(_ideal);", output);
        Assert.Contains("EdgeConstraintGenerator.generateEdgeConstraints(graph.getEdges(), _ideal.clone(), horizontalSolver, verticalSolver);", output);
    }
}
