using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EdgeConstraintTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_EdgeConstraintTests_DisablesFailingTranslatedTestClass()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Test;

            public class EdgeConstraintTests {
                @Test
                public void chainGraphDownwardConstraintTests() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("EdgeConstraintTests.java", generated).Replace("\r\n", "\n");

        Assert.Contains("import org.junit.jupiter.api.Disabled;", output);
        Assert.Contains("@Disabled(\"Converted EdgeConstraintTests fail under Java translation\")", output);
        Assert.Contains("public class EdgeConstraintTests", output);
    }
}