using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IncrementalSugiyamaCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_IncrementalSugiyamaTests_DisablesClass()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Test;

            public class IncrementalSugiyamaTests extends MsaglTestBase {
                @Test
                public void nodeShapeChange() {
                    String filePath = Paths.combine(getTestContext().TestRunDirectory, "Out\\Dots", "chat.dot");
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("IncrementalSugiyamaTests.java", generated).Replace("\r\n", "\n");

        Assert.Contains("@Disabled(\"Requires DOT file test infrastructure\")", output);
        Assert.Contains("import org.junit.jupiter.api.Disabled;", output);
    }
}