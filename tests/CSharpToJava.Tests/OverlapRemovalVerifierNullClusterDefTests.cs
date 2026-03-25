using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OverlapRemovalVerifierNullClusterDefTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_OverlapRemovalVerifier_FixesNullArraysAsList()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import java.util.Arrays;

            public class OverlapRemovalVerifier {
                public boolean checkResult(VariableDef[] variableDefs, ClusterDef[] clusterDefs, ConstraintDef[] constraintDefsX, ConstraintDef[] constraintDefsY, double[] expectedPositionsX, double[] expectedPositionsY, boolean checkResults) {
                    return checkResult(Arrays.asList(variableDefs), Arrays.asList(clusterDefs), Arrays.asList(constraintDefsX), Arrays.asList(constraintDefsY), checkResults);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("OverlapRemovalVerifier.java", generated);

        Assert.Contains("clusterDefs != null ? Arrays.asList(clusterDefs) : Collections.emptyList()", output);
        Assert.Contains("constraintDefsX != null ? Arrays.asList(constraintDefsX) : Collections.emptyList()", output);
        Assert.Contains("constraintDefsY != null ? Arrays.asList(constraintDefsY) : Collections.emptyList()", output);
        Assert.DoesNotContain("Arrays.asList(clusterDefs),", output);
    }
}
