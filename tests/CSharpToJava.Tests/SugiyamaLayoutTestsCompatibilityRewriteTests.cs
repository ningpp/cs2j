using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SugiyamaLayoutTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_SugiyamaLayoutTests_RewritesDirectoryGetFilesCall()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import java.io.*;
            import java.nio.file.Paths;
            import java.util.*;

            public class SugiyamaLayoutTests {
                public void test() {
                    String fileName = Paths.get(this.getTestContext().TestDir, "Out\\Dots\\fsm.dot").toString();
                    String[] allFiles = Files.getFiles(java.nio.file.Paths.get(this.getTestContext().TestDir, "Out\\Dots").toString(), "*.dot");
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("SugiyamaLayoutTests.java", generated);

        Assert.Contains("resolveTestDataPath(", output);
        Assert.Contains("DotFiles", output);
        Assert.Contains("LevFiles", output);
        Assert.Contains("fsm.dot", output);
        Assert.Contains("findTestDataFiles(", output);
        Assert.DoesNotContain("Files.getFiles(", output);
    }
}