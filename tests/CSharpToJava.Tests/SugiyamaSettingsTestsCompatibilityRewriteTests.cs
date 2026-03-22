using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SugiyamaSettingsTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_SugiyamaSettingsTests_WrapsCheckedCreateFromFileCall()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class SugiyamaSettingsTests {
                public void serializeDeserialize() {
                    GeometryGraph oldGraph = new GeometryGraph();
                    try {
                        GeometryGraphWriter.write(oldGraph, oldSettings, "settings.msagl.geom");
                    } catch (Exception e) {
                        throw new RuntimeException(e);
                    }
                    LayoutAlgorithmSettings baseSettings;
                    ObjectHolder<LayoutAlgorithmSettings> _baseSettingsHolder1 = new ObjectHolder<>();
                    GeometryGraphReader.createFromFile("settings.msagl.geom", _baseSettingsHolder1);
                    baseSettings = _baseSettingsHolder1.value;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("SugiyamaSettingsTests.java", generated);

        Assert.Contains("GeometryGraphWriter.write(oldGraph, oldSettings, \"settings.msagl.geom\");", output);
        Assert.Contains("try {", output);
        Assert.Contains("GeometryGraphReader.createFromFile(\"settings.msagl.geom\", _baseSettingsHolder1);", output);
        Assert.Contains("} catch (Exception e) {", output);
        Assert.Contains("throw new RuntimeException(e);", output);
    }
}