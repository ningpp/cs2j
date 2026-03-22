using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectilinearEdgeRouterWrapperCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearEdgeRouterWrapper_InitializesPointsInsidePadding()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            import java.util.ArrayList;

            public class RectilinearEdgeRouterWrapper {
                public void test() {
                    ArrayList<Point> pointsInsidePadding;
                    ObjectHolder<ArrayList<Point>> _pointsInsidePaddingHolder1 = new ObjectHolder<>();
                    if (this.noIntersectionPointsAreInsidePadding(edgeGeom, crossedObstacle, _pointsInsidePaddingHolder1)) {
                        pointsInsidePadding = _pointsInsidePaddingHolder1.value;
                        return;
                    }
                    this.printFailurePoints(edgeGeom, sourceObstacle, targetObstacle, crossedObstacle, pointsInsidePadding);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearEdgeRouterWrapper.java", generated);

        Assert.Contains("ArrayList<Point> pointsInsidePadding = null;", output);
        Assert.DoesNotContain("ArrayList<Point> pointsInsidePadding;", output);
    }
}