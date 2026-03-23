using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class AspectRatioTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_AspectRatioTests_RewritesDirectoryPathToConcreteDotFile()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class AspectRatioTests {
                public void test() {
                    String filePath = java.nio.file.Paths.get(this.getTestContext().TestDir, "Out\\Dots").toString();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("AspectRatioTests.java", generated);

        Assert.Contains("resolveTestDataPath(", output);
        Assert.Contains("DotFiles", output);
        Assert.Contains("LevFiles", output);
        Assert.Contains("chat.dot", output);
        Assert.DoesNotContain("Out\\Dots", output);
    }
}