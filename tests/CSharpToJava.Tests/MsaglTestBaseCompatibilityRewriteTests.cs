using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MsaglTestBaseCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_MsaglTestBase_AddsTestDataResolutionHelpers()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import java.io.*;
            import java.util.*;

            public class MsaglTestBase {
                protected GeometryGraph loadGraph(String geometryGraphFileName, ObjectHolder<LayoutAlgorithmSettings> settings) {
                    if (StringHelper.isNullOrEmpty(geometryGraphFileName)) {
                    throw new NullPointerException("geometryGraphFileName");
                    }
                    GeometryGraph graph = null;
                    settings.value = null;
                    if (geometryGraphFileName.endsWith(".geom")) {
                    graph = GeometryGraphReader.createFromFile(geometryGraphFileName, settings);
                    }
                    return graph;
                }
                protected static RelativeFloatingPort makePort(Node node) {
                    return null;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("MsaglTestBase.java", generated);

        Assert.Contains("geometryGraphFileName = resolveTestDataPath(geometryGraphFileName);", output);
        Assert.Contains("if (resolvedGraphPath.isDirectory()) {", output);
        Assert.Contains("String[] dotFiles = findTestDataFiles(geometryGraphFileName, \"*.dot\");", output);
        Assert.Contains("protected static String resolveTestDataPath(String fileName)", output);
        Assert.Contains("protected static String[] findTestDataFiles(String relativeDir, String glob)", output);
        Assert.Contains("sorted(String::compareToIgnoreCase)", output);
        Assert.Contains("new File(basePath, \"src/test/resources/Resources\").getPath()", output);
    }
}