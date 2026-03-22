using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class InitialLayoutTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_InitialLayoutTests_RewritesHashSetOverNonCollections()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class InitialLayoutTests {
                public void test() {
                    var outerCluster = createCluster(graph.getNodes().stream().limit(4).filter(x -> !new HashSet<>(innerCluster.getNodes()).contains(x)).collect(java.util.stream.Collectors.toList()), Margin);
                    graph.setRootCluster(new Cluster(graph.getNodes().stream().filter(x -> !new HashSet<>(graph.getNodes().stream().limit(4)).contains(x)).collect(java.util.stream.Collectors.toList())));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("InitialLayoutTests.java", generated);

        Assert.Contains("!StreamSupport.stream(innerCluster.getNodes().spliterator(), false).collect(java.util.stream.Collectors.toSet()).contains(x)", output);
        Assert.Contains("!graph.getNodes().stream().limit(4).collect(java.util.stream.Collectors.toSet()).contains(x)", output);
        Assert.DoesNotContain("new HashSet<>(innerCluster.getNodes())", output);
        Assert.DoesNotContain("new HashSet<>(graph.getNodes().stream().limit(4))", output);
    }
}