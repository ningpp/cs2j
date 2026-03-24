using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectangularClusterBoundaryCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectangularClusterBoundary_InitializesRectangleStructField()
    {
        const string generated = """
            package Microsoft.Msagl.Core.Geometry;

            public class RectangularClusterBoundary {
                public Rectangle rectangle;
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectangularClusterBoundary.java", generated);

        Assert.Contains("public Rectangle rectangle = new Rectangle();", output);
        Assert.DoesNotContain("public Rectangle rectangle;", output);
    }
}