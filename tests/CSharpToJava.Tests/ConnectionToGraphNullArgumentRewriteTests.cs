using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Reflection;

namespace CSharpToJava.Tests;

public class ConnectionToGraphNullArgumentRewriteTests
{
    [Fact]
    public async Task ProjectPipeline_DoesNotAppendNullToConnectionToGraphCall()
    {
        const string code = """
            namespace Microsoft.Msagl.Drawing {
                public enum ConnectionToGraph { Connected, Disconnected }

                public class Graph {}
                public class GeometryGraph {}
                public class Node {}

                public class GeometryGraphCreator {
                    public void Process(Graph drawingGraph, GeometryGraph msaglGraph, Node n) {
                        CreateGeometryNode(drawingGraph, msaglGraph, n,
                            ConnectionToGraph.Connected);
                    }

                    public static Node CreateGeometryNode(Graph drawingGraph, GeometryGraph geometryGraph, Node node,
                        ConnectionToGraph connection) {
                        return node;
                    }
                }
            }
            """;

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java17 });
        var files = new[] { new SourceFile { FilePath = "GeometryGraphCreator.cs", Content = code } };

        var results = await pipeline.ConvertProjectAsync(files);
        var converted = results.FirstOrDefault(r => r.FileName?.EndsWith("GeometryGraphCreator.java", StringComparison.Ordinal) == true);

        Assert.NotNull(converted);
        Assert.NotNull(converted!.GeneratedCode);
        Assert.Contains("createGeometryNode(drawingGraph, msaglGraph, n,", converted.GeneratedCode);
        Assert.Contains("ConnectionToGraph.Connected);", converted.GeneratedCode);
        Assert.DoesNotContain("ConnectionToGraph.Connected, null);", converted.GeneratedCode);
    }

    [Fact]
    public async Task ProjectPipeline_AppendsNullForThreeArgEdgeCtorWithConnectionToGraph()
    {
        const string code = """
            namespace Microsoft.Msagl.Drawing {
                public enum ConnectionToGraph { Connected, Disconnected }

                public class Node {}
                public class EdgeAttr {}

                public class Edge {
                    public Edge(Node source, Node target, ConnectionToGraph connection, EdgeAttr attr) {}
                }

                public class LayoutEditor {
                    public void Insert(Node source, Node target) {
                        var e = new Edge(source, target, ConnectionToGraph.Connected);
                    }
                }
            }
            """;

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java17 });
        var files = new[] { new SourceFile { FilePath = "LayoutEditor.cs", Content = code } };

        var results = await pipeline.ConvertProjectAsync(files);
        var converted = results.FirstOrDefault(r => r.FileName?.EndsWith("LayoutEditor.java", StringComparison.Ordinal) == true);

        Assert.NotNull(converted);
        Assert.NotNull(converted!.GeneratedCode);
        Assert.Contains("new Edge(source, target, ConnectionToGraph.Connected, null);", converted.GeneratedCode);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesSvgGraphWriterStreamsToOutputStream()
    {
        const string snippet = """
            InputStream stream;
            public SvgGraphWriter(InputStream streamPar, Graph graphP) {
            }
            public InputStream getStream() {
                return stream;
            }
            public void setStream(InputStream value) {
                stream = value;
            }
            public static void writeAllExceptEdges(Graph graph, String outputFile) {
            }
            public static void write(Graph graph, String outputFile, Function<String, String> nodeSanitizer, Function<String, String> attrSanitizer, int precision) {
            }
            public static void writeAllExceptEdgesInBlack(Graph graph, String outputFile) {
            }
            try (FileInputStream stream = FileHelper.create(outputFile)) {
            }
            """;
        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "SvgGraphWriter.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        Assert.Contains("OutputStream stream;", results[0].GeneratedCode);
        Assert.Contains("public SvgGraphWriter(OutputStream streamPar, Graph graphP)", results[0].GeneratedCode);
        Assert.Contains("public OutputStream getStream()", results[0].GeneratedCode);
        Assert.Contains("public void setStream(OutputStream value)", results[0].GeneratedCode);
        Assert.Contains("public static void writeAllExceptEdges(Graph graph, String outputFile) throws Exception", results[0].GeneratedCode);
        Assert.Contains("public static void write(Graph graph, String outputFile, Function<String, String> nodeSanitizer, Function<String, String> attrSanitizer, int precision) throws Exception", results[0].GeneratedCode);
        Assert.Contains("public static void writeAllExceptEdgesInBlack(Graph graph, String outputFile) throws Exception", results[0].GeneratedCode);
        Assert.Contains("try (OutputStream stream = FileHelper.create(outputFile))", results[0].GeneratedCode);
        Assert.DoesNotContain("InputStream stream;", results[0].GeneratedCode);
        Assert.DoesNotContain("FileInputStream stream = FileHelper.create(outputFile)", results[0].GeneratedCode);
    }

    [Fact]
    public async Task ProjectPipeline_GeneratesXmlReaderWithLocalReadStateWrapper()
    {
        const string code = "namespace Demo { public class Placeholder { } }";

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java17 });
        var files = new[] { new SourceFile { FilePath = "Placeholder.cs", Content = code } };

        var results = await pipeline.ConvertProjectAsync(files);
        var xmlReader = results.FirstOrDefault(r => string.Equals(r.FileName, "XmlReader.java", StringComparison.Ordinal));

        Assert.NotNull(xmlReader);
        Assert.NotNull(xmlReader!.GeneratedCode);
        Assert.Contains(".ReadState.getClosed();", xmlReader.GeneratedCode);
        Assert.Contains(".ReadState.getInteractive()", xmlReader.GeneratedCode);
        Assert.Contains(".ReadState.getEndOfFile()", xmlReader.GeneratedCode);
        Assert.Contains(".ReadState.getError()", xmlReader.GeneratedCode);
        Assert.DoesNotContain("Microsoft.Msagl.ReadState", xmlReader.GeneratedCode);
    }

    [Fact]
    public void CompatibilityRewrites_FixesAttributeValuePairSplitPattern()
    {
        const string snippet = "return Arrays.stream(txt.split(\"[ ,\n        ;\t]\")).filter(s -> !s.isEmpty()).toArray(String[]::new);";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "AttributeValuePair.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("txt.split(", output);
        Assert.Contains("\\n;", output);
        Assert.DoesNotContain("txt.split(\"[ ,\n", output);
    }

    [Fact]
    public void CompatibilityRewrites_QualifiesValueTypeCellReferences()
    {
        const string snippet = "public Cell<String> sList;\npublic Cell<Cell<String>> sLists;";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "ValueType.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("public Parser.Cell<String> sList;", output);
        Assert.Contains("public Parser.Cell<Parser.Cell<String>> sLists;", output);
        Assert.DoesNotContain("public Cell<String> sList;", output);
    }

    [Fact]
    public void CompatibilityRewrites_QualifiesAttributeValuePairLabelType()
    {
        const string snippet = "public static AbstractMap.SimpleEntry<Label, GraphAttr> addGraphAttrs(AbstractMap.SimpleEntry<Label, GraphAttr> couple, ArrayList arrayList) { return couple; }";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "AttributeValuePair.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("AbstractMap.SimpleEntry<Microsoft.Msagl.Drawing.Label, GraphAttr>", output);
        Assert.DoesNotContain("SimpleEntry<Label, GraphAttr>", output);
    }
}
