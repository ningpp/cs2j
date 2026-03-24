using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IncrementalSugiyamaCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_IncrementalSugiyamaTests_DoesNotDisableClass()
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

        Assert.DoesNotContain("@Disabled(\"Converted IncrementalSugiyamaTests fail under Java translation\")", output);
        Assert.DoesNotContain("import org.junit.jupiter.api.Disabled;", output);
    }
}