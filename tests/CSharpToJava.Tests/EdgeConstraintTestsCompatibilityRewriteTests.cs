using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EdgeConstraintTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_EdgeConstraintTests_PreservesRunnableTestClassAndRewritesMessages()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Test;

            public class EdgeConstraintTests {
                @Test
                public void chainGraphDownwardConstraintTests() {
                    Assertions.assertTrue(edge.getTarget().getCenter().Y > edge.getSource().getCenter().Y, String.format("Edge from source {0} to target {1} does not follow downward rule", edge.getSource().getUserData(), edge.getTarget().getUserData()));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("EdgeConstraintTests.java", generated).Replace("\r\n", "\n");

        Assert.DoesNotContain("import org.junit.jupiter.api.Disabled;", output);
        Assert.DoesNotContain("@Disabled(", output);
        Assert.Contains("String.format(\"Edge from source %s to target %s does not follow downward rule\"", output);
        Assert.Contains("public class EdgeConstraintTests", output);
    }
}