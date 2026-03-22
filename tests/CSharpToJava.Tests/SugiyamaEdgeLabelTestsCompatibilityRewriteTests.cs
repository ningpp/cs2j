using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SugiyamaEdgeLabelTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_SugiyamaEdgeLabelTests_RewritesExtensionMethodCall()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.Point;

            public class SugiyamaEdgeLabelTests {
                private static void verifyLabelIsNearEdges(Microsoft.Msagl.Core.Layout.Edge edge) {
                    Point[] edgePoints = edge.getPoints();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("SugiyamaEdgeLabelTests.java", generated);

        Assert.Contains("Point[] edgePoints = EdgeExtensions.getPoints(edge);", output);
        Assert.DoesNotContain("edge.getPoints()", output);
    }
}