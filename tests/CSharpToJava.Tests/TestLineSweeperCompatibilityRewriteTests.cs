using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TestLineSweeperCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_TestLineSweeper_MaterializesOutEdgesIterableIntoArrayList()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Routing;

            import Microsoft.Msagl.Routing.Visibility.VisibilityEdge;
            import java.util.ArrayList;
            import Collectors;
            import java.util.stream.StreamSupport;

            public class TestLineSweeper {
                public void test(VisibilityVertex orig) {
                    var outOrig = new ArrayList<VisibilityEdge>(orig.getOutEdges());
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("TestLineSweeper.java", generated);

        Assert.Contains("StreamSupport.stream(orig.getOutEdges().spliterator(), false).collect(Collectors.toCollection(ArrayList::new))", output);
        Assert.DoesNotContain("new ArrayList<VisibilityEdge>(orig.getOutEdges())", output);
    }
}