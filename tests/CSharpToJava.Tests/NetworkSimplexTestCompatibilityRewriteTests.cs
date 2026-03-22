using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NetworkSimplexTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_NetworkSimplexTest_RewritesBiFunctionPrimitiveLambdaParameters()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class NetworkSimplexTest {
                public void test() {
                    BiFunction<Integer, Integer, PolyIntEdge> edge = (int x, int y) -> {
                        return new PolyIntEdge(x, y, null);
                    };
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("NetworkSimplexTest.java", generated);

        Assert.Contains("BiFunction<Integer, Integer, PolyIntEdge> edge = (Integer x, Integer y) -> {", output);
        Assert.DoesNotContain("(int x, int y)", output);
    }
}