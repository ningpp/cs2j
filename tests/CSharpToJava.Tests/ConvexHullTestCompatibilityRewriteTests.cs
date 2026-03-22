using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConvexHullTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_ConvexHullTest_RewritesArraySpliteratorToArraysAsList()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class ConvexHullTest {
                public void calculateConvexHullTest() {
                    var expected = new Point[] { new Point(0, 0) };
                    var points = new ArrayList<Point>();
                    points.addAll(StreamSupport.stream(expected.spliterator(), false).collect(Collectors.toCollection(ArrayList::new)));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("ConvexHullTest.java", generated);

        Assert.Contains("points.addAll(Arrays.asList(expected));", output);
        Assert.DoesNotContain("expected.spliterator()", output);
    }
}