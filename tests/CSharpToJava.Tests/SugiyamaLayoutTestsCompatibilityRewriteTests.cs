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
                    String[] allFiles = Files.getFiles(java.nio.file.Paths.get(this.getTestContext().TestDir, "Out\\Dots").toString(), "*.dot");
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("SugiyamaLayoutTests.java", generated);

        Assert.Contains("Optional.ofNullable(new File(", output);
        Assert.Contains("getPathMatcher(\"glob:*.dot\").matches(java.nio.file.Paths.get(name))", output);
        Assert.DoesNotContain("Files.getFiles(", output);
    }
}