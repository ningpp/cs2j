using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectilinearTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearTests_RewritesObjectEmptyToShapeArray()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearTests {
                public void test(RectilinearEdgeRouterWrapper router) {
                    this.verifyAllObstaclesInConvexHull(router, Object.empty());
                    this.verifyAllObstaclesInClump(router, Object.empty());
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearTests.java", generated);

        Assert.Contains("this.verifyAllObstaclesInConvexHull(router, new Shape[0]);", output);
        Assert.Contains("this.verifyAllObstaclesInClump(router, new Shape[0]);", output);
        Assert.DoesNotContain("Object.empty()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearTests_RewritesIntStreamMapToMapToObj()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearTests {
                public void test(int[] siblingIndexes, java.util.ArrayList<Shape> obstacles, RectilinearEdgeRouterWrapper router) {
                    this.verifyAllObstaclesInClump(router, Arrays.stream(siblingIndexes).map(idx -> obstacles.get(idx)).toArray(Shape[]::new), false);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearTests.java", generated);

        Assert.Contains("Arrays.stream(siblingIndexes).mapToObj(idx -> obstacles.get(idx)).toArray(Shape[]::new)", output);
        Assert.DoesNotContain("Arrays.stream(siblingIndexes).map(idx -> obstacles.get(idx))", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearTests_RewritesModifiedClosureOffsetCapture()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearTests {
                public void test(Shape b) {
                    var offset = new Point(0, 0);
                    var portB = new RelativeFloatingPort(() -> b.getBoundaryCurve(), () -> Point.add(b.getBoundingBox().getCenter(), offset), new Point(0, 0));
                    offset = new Point(-5, b.getBoundingBox().getTop() - b.getBoundingBox().getCenter().Y);
                    offset = new Point(-10, b.getBoundingBox().getBottom() - b.getBoundingBox().getCenter().Y);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearTests.java", generated);

        Assert.Contains("final Point[] offset = new Point[] { new Point(0, 0) };", output);
        Assert.Contains("() -> Point.add(b.getBoundingBox().getCenter(), offset[0])", output);
        Assert.Contains("offset[0] = new Point(-5, b.getBoundingBox().getTop() - b.getBoundingBox().getCenter().Y);", output);
        Assert.Contains("offset[0] = new Point(-10, b.getBoundingBox().getBottom() - b.getBoundingBox().getCenter().Y);", output);
    }
}