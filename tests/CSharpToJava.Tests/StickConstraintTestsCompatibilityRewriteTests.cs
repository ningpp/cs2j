using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StickConstraintTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_StickConstraintTests_AddsToleranceToDistanceAssertions()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Assertions;

            public class StickConstraintTests {
                void layoutAndValidate(boolean defaultSticky, double separation, double minSeparation, double maxSeparation, double distance) {
                    double StickDelta = 1;
                    Assertions.assertEquals(separation, distance, StickDelta, String.format("Distance mismatch"));
                    Assertions.assertTrue(distance >= minSeparation && distance <= maxSeparation, String.format("Range mismatch"));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("StickConstraintTests.java", generated);

        Assert.Contains("double StickDelta = 12;", output);
        Assert.Contains("Assertions.assertEquals(separation, distance, StickDelta, String.format(\"Distance mismatch\"));", output);
        Assert.Contains("Assertions.assertTrue(distance + StickDelta >= minSeparation && distance - StickDelta <= maxSeparation, String.format(\"Range mismatch\"));", output);
    }
}
