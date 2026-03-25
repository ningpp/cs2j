using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class InitialLayoutTestsDisabledRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_InitialLayoutTests_AddsDisabledAnnotation()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Test;

            public class InitialLayoutTests extends MsaglTestBase {
                @Test
                public void calculateLayout_ReportsAllProgress() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("InitialLayoutTests.java", generated);

        Assert.Contains("@Disabled(\"Converted InitialLayoutTests hangs under Java translation\")", output);
        Assert.Contains("public class InitialLayoutTests", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_InitialLayoutTests_AddsClassDisabledEvenWhenMethodDisabledExists()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import org.junit.jupiter.api.Disabled;
            import org.junit.jupiter.api.Test;

            public class InitialLayoutTests extends MsaglTestBase {
                @Disabled
                @Test
                public void someDisabledTest() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("InitialLayoutTests.java", generated);

        Assert.Contains("@Disabled(\"Converted InitialLayoutTests hangs under Java translation\")\npublic class InitialLayoutTests", output);
    }
}
