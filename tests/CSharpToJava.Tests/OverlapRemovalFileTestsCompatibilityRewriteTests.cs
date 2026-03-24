using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OverlapRemovalFileTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_OverlapRemovalFileTests_RewritesDataFilePathWithoutDisablingClass()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import org.junit.jupiter.api.Disabled;
            import org.junit.jupiter.api.Test;

            @Disabled("Converted OverlapRemovalFileTests fail under Java translation")
            public class OverlapRemovalFileTests extends OverlapRemovalVerifier {
                private void runTestDataFile(String fileName) {
                    var pathAndFileSpec = java.nio.file.Paths.get(getTestContext().DeploymentDirectory, "Constraints\\OverlapRemoval\\Data").toString();
                    var testFileReader = this.loadTestDataFile(pathAndFileSpec);
                }

                @Test
                public void test() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("OverlapRemovalFileTests.java", generated).Replace("\r\n", "\n");

        Assert.Contains("java.nio.file.Paths.get(getTestContext().DeploymentDirectory, \"Constraints\\\\OverlapRemoval\\\\Data\", fileName).toString()", output);
        Assert.DoesNotContain("\"Constraints\\OverlapRemoval\\Data\", fileName", output);
        Assert.DoesNotContain("@Disabled(\"Converted OverlapRemovalFileTests fail under Java translation\")", output);
    }
}