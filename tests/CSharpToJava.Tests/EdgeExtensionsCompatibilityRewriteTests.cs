using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EdgeExtensionsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_EdgeExtensions_RewritesExtensionSelfCall()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public final class EdgeExtensions {
                public static Point[] getPoints(Microsoft.Msagl.Core.Layout.Edge edge) {
                    return edge.getPoints(1000);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("EdgeExtensions.java", generated);

        Assert.Contains("return getPoints(edge, 1000);", output);
        Assert.DoesNotContain("return edge.getPoints(1000);", output);
    }
}