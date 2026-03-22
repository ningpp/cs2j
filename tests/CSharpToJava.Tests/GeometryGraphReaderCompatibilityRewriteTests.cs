using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GeometryGraphReaderCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewrites_ShouldWrapGeometryGraphReaderCheckedExceptions()
    {
        const string generatedCode = """
            public static GeometryGraph createFromFile(String fileName) throws Exception {
                    LayoutAlgorithmSettings settings;
                    ObjectHolder<LayoutAlgorithmSettings> _settingsHolder1 = new ObjectHolder<>();
                    var _ret = createFromFile(fileName, _settingsHolder1);
                    settings = _settingsHolder1.value;
                    return _ret;
                }
            public static GeometryGraph createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings) throws Exception {
                    if (firstCharacter(fileName) != '<') {
                    settings.value = null;
                    return null;
                    }
                    try (InputStream stream = FileHelper.openRead(fileName)) {
                    var graphReader = new GeometryGraphReader(stream);
                    GeometryGraph graph = graphReader.read();
                    settings.value = graphReader.getSettings();
                    return graph;
                    }
                }
            static char firstCharacter(String fileName) throws Exception {
                    try (TextReader reader = FileHelper.openText(fileName)) {
                    var first = (char)(reader.peek());
                    return first;
                    }
                }
            """;

        var rewritten = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("GeometryGraphReader.java", generatedCode);

        Assert.Contains("createFromFile(String fileName", rewritten);
        Assert.Contains("createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings)", rewritten);
        Assert.Contains("firstCharacter(String fileName)", rewritten);
    }
}