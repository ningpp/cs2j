using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OverlapRemovalTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_OverlapRemovalTests_RewritesClassInitializeWithoutDisablingClass()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Constraints;

            import org.junit.jupiter.api.BeforeAll;
            import org.junit.jupiter.api.Test;
            import org.junit.jupiter.api.Disabled;
            import Microsoft.VisualStudio.TestTools.UnitTesting.*;

            @Disabled("Converted OverlapRemovalTests fail under Java translation")
            public class OverlapRemovalTests extends OverlapRemovalVerifier {
                @BeforeAll
                public static void classInitialize(TestContext testContext) {
                    ClusterDef.setTestContext(testContext);
                }

                @Test
                public void test_AllVertical_Pad0() {
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("OverlapRemovalTests.java", generated).Replace("\r\n", "\n");

        Assert.Contains("@BeforeAll\npublic static void classInitialize() {\n        classInitialize(new TestContext());\n    }\n        public static void classInitialize(TestContext testContext) {", output);
        Assert.Contains("ClusterDef.setTestContext(testContext);", output);
        Assert.DoesNotContain("@Disabled(\"Converted OverlapRemovalTests fail under Java translation\")", output);
        Assert.Contains("import org.junit.jupiter.api.Disabled;", output);
    }
}