using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RTreeTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RTreeTest_RewritesFormattedAssertionArguments()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class RTreeTest {
                public void test() {
                    Assertions.assertEquals(result.size(), checkList.size(), "result and check are different sizes: seed={0}", seed);
                    Assertions.assertTrue(rect.intersects(r), "rect doesn't intersect query: seed={0}, rect={1}, query={2}", seed, r, rect);
                    Assertions.assertTrue(checkSet.contains(r.toString()), "check set does not contain rect: seed={0}", seed);
                    Assertions.assertTrue(rect.intersects(r), "rect doesn't intersect query: rect={1}, query={2}", r, rect);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RTreeTest.java", generated);

        Assert.Contains("Assertions.assertEquals(result.size(), checkList.size(), String.format(\"result and check are different sizes: seed=%s\", seed));", output);
        Assert.Contains("Assertions.assertTrue(rect.intersects(r), String.format(\"rect doesn't intersect query: seed=%s, rect=%s, query=%s\", seed, r, rect));", output);
        Assert.Contains("Assertions.assertTrue(checkSet.contains(r.toString()), String.format(\"check set does not contain rect: seed=%s\", seed));", output);
        Assert.Contains("Assertions.assertTrue(rect.intersects(r), String.format(\"rect doesn't intersect query: rect=%s, query=%s\", r, rect));", output);
        Assert.DoesNotContain("seed={0}", output);
    }
}