using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OverlapRemovalVerifierCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_OverlapRemovalVerifier_RewritesClusterDumpCall()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            public class OverlapRemovalVerifier {
                public void verify(Iterable<ClusterDef> iterClusterDefs) {
                    dumpRectangles( iterClusterDefs );
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("OverlapRemovalVerifier.java", generated);

        Assert.Contains("dumpClusterRectangles(iterClusterDefs);", output);
        Assert.DoesNotContain("dumpRectangles(iterClusterDefs);", output);
    }
}