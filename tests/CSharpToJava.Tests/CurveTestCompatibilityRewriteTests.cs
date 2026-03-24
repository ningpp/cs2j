using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CurveTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_CurveTest_NoLongerDisablesClass()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Disabled;
            import org.junit.jupiter.api.Test;

            @Disabled("Converted CurveTest fails under Java translation")
            public class CurveTest {
                @Test
                public void circleLineCross() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("CurveTest.java", generated).Replace("\r\n", "\n");

        Assert.DoesNotContain("@Disabled(\"Converted CurveTest fails under Java translation\")", output);
        Assert.Contains("public class CurveTest", output);
        Assert.Contains("@Test", output);
    }
}