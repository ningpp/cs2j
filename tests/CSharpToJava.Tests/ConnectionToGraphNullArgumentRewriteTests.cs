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

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java25 });
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

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java25 });
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

        var pipeline = new ProjectConversionPipeline(new ConversionOptions { TargetJavaVersion = JavaVersion.Java25 });
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

    [Fact]
    public void CompatibilityRewrites_DisambiguatesAttributeValuePairEdgeAndGeometryLabel()
    {
        const string snippet = """
            import Microsoft.Msagl.Core.Layout.Edge;
            import Microsoft.Msagl.Core.Layout.Label;
            public static void addEdgeAttrs(ArrayList arrayList, Edge edge) { }
            static void addBezieSegsToEdgeFromPosData(Edge edge, ArrayList<Point> list) { }
            static void initGeomEdge(Edge edge) { }
            static void initGeomLabel(Microsoft.Msagl.Drawing.Label label) { label.setGeometryLabel(new Label()); }
            int st = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowParentheses;
            if (MathHelper.tryParseDouble(val, st, AttributeBase.getUSCultureInfo(), _resultHolder1)) { }
            String[] vals = split(val);
            av.val = tryParseDouble(get(vals, 0), name);
            av.val = Integer.parseInt(val, AttributeBase.getUSCultureInfo());
            av.val = Float.parseFloat(val, java.util.Locale.ROOT);
            x = Double.parseDouble(get(ret, 0), AttributeBase.getUSCultureInfo());
            y = Double.parseDouble(get(ret, 1), AttributeBase.getUSCultureInfo());
            z = Integer.parseInt(s, NumberStyles.AllowHexSpecifier, AttributeBase.getUSCultureInfo());
            Match m = Regex.match(v, "setlinewidth\\((\\d+)\\)");
            if (!m.Success) {
            return false;
            }
            lw.value = (int)(getNumber(m.Groups.get(1).Value));
            return Color.fromArgb(gleeColor.getA(), gleeColor.getR(), gleeColor.getG(), gleeColor.getB());
            return Color.fromArgb(toByte(r), toByte(g), toByte(b));
            return Color.fromArgb(r, g, b);
            return Color.fromArgb(a, r, g, b);
            return new Color(drawingColor.A, drawingColor.R, drawingColor.G, drawingColor.B);
            edgeAttr.setWeight(Integer.parseInt((attrVal.val instanceof String ? (String)(attrVal.val) : null) /* result may be null — check before use */, AttributeBase.getUSCultureInfo()));
            p.X = Double.parseDouble(x, AttributeBase.getUSCultureInfo());
            p.Y = Double.parseDouble(y, AttributeBase.getUSCultureInfo());
            Color ret = Color.fromName(val);
            if (ret.A == 0 && ret.R == 0 && ret.B == 0 && ret.G == 0) {
            return Color.Black;
            }
            return ret;
            """;

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
        Assert.DoesNotContain("import Microsoft.Msagl.Core.Layout.Edge;", output);
        Assert.DoesNotContain("import Microsoft.Msagl.Core.Layout.Label;", output);
        Assert.Contains("addEdgeAttrs(ArrayList arrayList, Microsoft.Msagl.Drawing.Edge edge)", output);
        Assert.Contains("addBezieSegsToEdgeFromPosData(Microsoft.Msagl.Drawing.Edge edge, ArrayList<Point> list)", output);
        Assert.Contains("initGeomEdge(Microsoft.Msagl.Drawing.Edge edge)", output);
        Assert.Contains("label.setGeometryLabel(new Microsoft.Msagl.Core.Layout.Label());", output);
        Assert.DoesNotContain("NumberStyles.AllowDecimalPoint", output);
        Assert.Contains("MathHelper.tryParseDouble(val, _resultHolder1)", output);
        Assert.Contains("var _marginVals = split(val);", output);
        Assert.Contains("tryParseDouble(get(_marginVals, 0), name)", output);
        Assert.Contains("Integer.parseInt(val)", output);
        Assert.Contains("Float.parseFloat(val)", output);
        Assert.Contains("Double.parseDouble(get(ret, 0))", output);
        Assert.Contains("Double.parseDouble(get(ret, 1))", output);
        Assert.Contains("Integer.parseInt(s, 16)", output);
        Assert.Contains("java.util.regex.Matcher m = java.util.regex.Pattern.compile(\"setlinewidth", output);
        Assert.Contains("lw.value = (int)(getNumber(m.group(1)));", output);
        Assert.Contains("new Color(gleeColor.getA(), gleeColor.getR(), gleeColor.getG(), gleeColor.getB())", output);
        Assert.Contains("new Color((byte)toByte(r), (byte)toByte(g), (byte)toByte(b))", output);
        Assert.Contains("new Color((byte)r, (byte)g, (byte)b)", output);
        Assert.Contains("new Color((byte)a, (byte)r, (byte)g, (byte)b)", output);
        Assert.Contains("new Color(drawingColor.getA(), drawingColor.getR(), drawingColor.getG(), drawingColor.getB())", output);
        Assert.Contains("Integer.parseInt((attrVal.val instanceof String ? (String)(attrVal.val) : null) /* result may be null — check before use */)", output);
        Assert.Contains("Double.parseDouble(x)", output);
        Assert.Contains("Double.parseDouble(y)", output);
        Assert.Contains("return Color.getBlack();", output);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesBufferExceptionSerializationSignature()
    {
        const string snippet = "protected BufferException(SerializationInfo info, StreamingContext context) { super(); }";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "BufferException.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("BufferException(Object info, Object context)", output);
        Assert.DoesNotContain("SerializationInfo", output);
        Assert.DoesNotContain("StreamingContext", output);
    }

    [Fact]
    public void CompatibilityRewrites_DisambiguatesParserNodeImportsAndGeomNodeType()
    {
        const string snippet = """
            import Microsoft.Msagl.Core.Layout.Edge;
            import Microsoft.Msagl.Core.Layout.Node;
            import Microsoft.Msagl.Drawing.Edge;
            import Microsoft.Msagl.Drawing.Node;
            protected void initialize() { }
            Node geomNode;
            ObjectHolder<Node> _geomNodeHolder1 = new ObjectHolder<>();
            Parser parser = new Parser();
            Scanner scanner = new Scanner(reader);
            parser.setScanner(scanner);
            for (String d : dst.toArray(String[]::new)) { }
            for (String s : src.toArray(String[]::new)) { }
            try (InputStream reader = new FileInputStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read)) { }
            CurrentSemanticValue.sList = mkEdgeStmt(getValueStack().get(getValueStack().getDepth() - 3).sList, getValueStack().get(getValueStack().getDepth() - 2).sLists, getValueStack().get(getValueStack().getDepth() - 1).aVal);
            void mkEdgeStmt(Cell<String> src, Cell<String> dst, ArrayList attrs) { }
            public static Graph parse(String file, IntHolder line, IntHolder col, ObjectHolder<String> msg) {
            try (InputStream reader = new FileInputStream(file)) {
            return Parser.parse(reader, line, col, msg);
            }
            }
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "Parser.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.DoesNotContain("import Microsoft.Msagl.Core.Layout.Edge;", output);
        Assert.DoesNotContain("import Microsoft.Msagl.Core.Layout.Node;", output);
        Assert.Contains("Microsoft.Msagl.Core.Layout.Node geomNode;", output);
        Assert.Contains("ObjectHolder<Microsoft.Msagl.Core.Layout.Node> _geomNodeHolder1 = new ObjectHolder<>();", output);
        Assert.Contains("public Parser(AbstractScanner<ValueType, LexLocation> scanner)", output);
        Assert.Contains("Parser parser = new Parser(scanner);", output);
        Assert.DoesNotContain("parser.setScanner(scanner);", output);
        Assert.Contains("dst.toArray())", output);
        Assert.Contains("src.toArray())", output);
        Assert.Contains("new FileInputStream(file))", output);
        Assert.Contains("mkEdgeStmtNested", output);
        Assert.Contains("Cell<String> mkEdgeStmtNested(Cell<String> src, Cell<Cell<String>> dst, ArrayList attrs)", output);
        Assert.Contains("} catch (Exception e) {", output);
        Assert.Contains("msg.value = e.getMessage();", output);
    }

    [Fact]
    public void CompatibilityRewrites_WrapsBlockReaderFactoryIoExceptions()
    {
        const string snippet = "int count = stream.read(b, 0, number);";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "BlockReaderFactory.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("try {", output);
        Assert.Contains("count = stream.read(b, 0, number);", output);
        Assert.Contains("} catch (IOException e) {", output);
        Assert.Contains("throw new RuntimeException(e);", output);
        Assert.Contains("if (count < 0) {", output);
        Assert.Contains("return 0;", output);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesBufferExceptionSerializationSuperCall()
    {
        const string snippet = "class BufferException extends Exception { protected BufferException(Object info, Object context) { super(info, context); } }";

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "BufferException.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("class BufferException extends RuntimeException", output);
        Assert.Contains("super(info != null ? info.toString() : null);", output);
        Assert.DoesNotContain("super(info, context);", output);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesBuildBufferApis()
    {
        const string snippet = """
            setFileName(fStrm.getName());
            BufferedReader rdr = (NextBlk.getTarget() instanceof BufferedReader ? (BufferedReader)(NextBlk.getTarget()) : null) /* result may be null — check before use */;
            return ((rdr == null ? "raw-bytes" : rdr.getCurrentEncoding().getBodyName()));
            return bldr.get(index - minIx);
            return next.get(index - brkIx);
            return bldr.toString(start - minIx, limit - start);
            return next.toString(start - brkIx, limit - start);
            return bldr.toString(start - minIx, brkIx - start) + next.toString(0, limit - brkIx);
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "BuildBuffer.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("setFileName(\"stream\");", output);
        Assert.Contains("return \"raw-bytes\";", output);
        Assert.Contains("bldr.charAt(index - minIx)", output);
        Assert.Contains("next.charAt(index - brkIx)", output);
        Assert.Contains("bldr.substring(start - minIx, limit - minIx)", output);
        Assert.Contains("next.substring(start - brkIx, limit - brkIx)", output);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesCodePageHandlingDotNetApis()
    {
        const string snippet = """
            String command = option.toUpperInvariant();
            if (command.startsWith("CodePage:", StringComparison.OrdinalIgnoreCase)) {
            }
            if (Character.isDigit(command.charAt(0))) {
            return Integer.parseInt(command, java.util.Locale.ROOT);
            }
            Charset enc = Charset.getEncoding(command);
            return enc.getCodePage();
            } catch (IllegalArgumentException _ex) {
            Console.Error.writeLine("Invalid format \"{0}\", using machine default", option);
            } catch (IllegalArgumentException _ex) {
            Console.Error.writeLine("Unknown code page \"{0}\", using machine default", option);
            }
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "CodePageHandling.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("option.toUpperCase(java.util.Locale.ROOT)", output);
        Assert.Contains("command.startsWith(\"CODEPAGE:\")", output);
        Assert.Contains("Character.isDigit(command.charAt(0))", output);
        Assert.Contains("Integer.parseInt(command)", output);
        Assert.Contains("Charset.forName(command);", output);
        Assert.Contains("System.err.printf(\"Invalid code page \\\"%s\\\", using machine default\", option);", output);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", output);
        Assert.DoesNotContain("Console.Error", output);
    }

    [Fact]
    public void CompatibilityRewrites_NormalizesScannerMaxParseTokenReflection()
    {
        const string snippet = """
            private static int getMaxParseToken() {
            Field f = Tokens.class.getField("maxParseToken");
            return ((Field.valueEquals(f, null) ? Integer.MAX_VALUE : (int)(f.getValue(null))));
            }
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "Scanner.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("Arrays.stream(Tokens.values()).mapToInt(Tokens::getValue).max()", output);
        Assert.DoesNotContain("Tokens.class.getField(\"maxParseToken\")", output);
    }

    [Fact]
    public void CompatibilityRewrites_RemovesUnreachableBreakAfterReturnInScanner()
    {
        const string snippet = """
            switch (state) {
            case 1:
                return Tokens.id;
                break;
            case 2:
                return Tokens.eof;
                break;
            }
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "Scanner.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("return Tokens.id;", output);
        Assert.Contains("return Tokens.eof;", output);
        Assert.DoesNotContain("return Tokens.id;\n        break;", output);
        Assert.DoesNotContain("return Tokens.eof;\n        break;", output);
    }

    [Fact]
    public void CompatibilityRewrites_FixesDot2SvgMainQuotedFormatString()
    {
        const string snippet = """
            System.out.println(String.format("File does not exist "%s"", filename));
            """;

        var results = new List<ConversionResult>
        {
            new ConversionResult
            {
                Success = true,
                FileName = "Dot2SvgMain.java",
                GeneratedCode = snippet
            }
        };

        var method = typeof(ProjectConversionPipeline).GetMethod(
            "ApplyCompatibilityRewrites",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, new object[] { results });

        var output = results[0].GeneratedCode;
        Assert.Contains("String.format(\"File does not exist \\\"%s\\\"\", filename)", output);
    }
}
