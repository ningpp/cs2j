using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping.JavaModel;
using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// C# IDisposable.Dispose() maps to Java AutoCloseable.close(). Instead of adding
/// checked-exception throws declarations, method bodies are wrapped with try-catch
/// to re-throw as RuntimeException, matching C# unchecked-exception semantics.
/// </summary>
public class DisposeCheckedExceptionTests
{
    private readonly ITestOutputHelper _out;
    public DisposeCheckedExceptionTests(ITestOutputHelper output) { _out = output; }

    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void DisposeBool_NoThrows_BodyMayBeWrapped()
    {
        var r = Convert(@"
using System;
class Resource : IDisposable {
    protected virtual void Dispose(bool disposing) {
        if (disposing) { }
    }
    public void Dispose() {
        Dispose(true);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // close methods should NOT have throws Exception (empty body, no checked exceptions)
        Assert.DoesNotContain("void close(boolean disposing) throws Exception", code);
        Assert.DoesNotContain("void close() throws Exception", code);
    }

    [Fact]
    public void DisposeNoArgs_NoThrows()
    {
        var r = Convert(@"
using System;
class Simple : IDisposable {
    public void Dispose() { }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.DoesNotContain("void close() throws Exception", code);
    }

    [Fact]
    public void TryWithResources_InputStream_WrapsBody()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(javaLibrary, diagnostics);

        var method = new JavaMethodDeclaration
        {
            Name = "readFile",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaRawStatement(
            "try (InputStream stream = FileHelper.openRead(fileName)) {\n    var data = stream.read();\n}"));

        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        // No throws — body is wrapped with try-catch
        Assert.Empty(method.ThrownExceptions);
        Assert.NotNull(method.StructuredBody);
        Assert.Single(method.StructuredBody.Statements);
        Assert.IsType<JavaTryCatchStatement>(method.StructuredBody.Statements[0]);
    }

    [Fact]
    public void TryWithResources_UnknownType_WrapsBody()
    {
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(javaLibrary, diagnostics);

        var method = new JavaMethodDeclaration
        {
            Name = "readCustom",
            ReturnType = "void",
            StructuredBody = new JavaMethodBody(),
        };
        method.StructuredBody.Statements.Add(new JavaRawStatement(
            "try (TextReader reader = FileHelper.openText(fileName)) {\n    var ch = reader.peek();\n}"));

        var clazz = new JavaClassDeclaration { Name = "TestClass" };
        clazz.Methods.Add(method);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        // Body should be wrapped with try-catch, no throws
        Assert.Empty(method.ThrownExceptions);
        Assert.NotNull(method.StructuredBody);
        Assert.Single(method.StructuredBody.Statements);
        Assert.IsType<JavaTryCatchStatement>(method.StructuredBody.Statements[0]);
    }

    [Fact]
    public void FullPipeline_StaticMethodWithUsing_WrapsBody()
    {
        var r = Convert(@"
using System;
using System.IO;
class GeometryReader : IDisposable {
    StreamReader xmlReader;
    static char FirstCharacter(string fileName) {
        using (TextReader reader = File.OpenText(fileName)) {
            var first = (char)reader.Peek();
            return first;
        }
    }
    protected virtual void Dispose(bool disposing) {
        if (disposing)
            xmlReader.Close();
    }
    public void Dispose() {
        Dispose(true);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // close methods should not have throws Exception
        Assert.DoesNotContain("void close(boolean disposing) throws Exception", code);
        Assert.DoesNotContain("void close() throws Exception", code);
        // firstCharacter should not have throws Exception (body is wrapped)
        var fcIdx = code.IndexOf("firstCharacter");
        if (fcIdx >= 0)
        {
            var snippet = code.Substring(Math.Max(0, fcIdx - 20), Math.Min(300, code.Length - Math.Max(0, fcIdx - 20)));
            _out.WriteLine(snippet);
        }
        Assert.DoesNotContain("firstCharacter(String fileName) throws Exception", code);
    }

    [Fact]
    public void OutParamMethodWithUsing_WrapsBody()
    {
        var r = Convert(@"
using System;
using System.IO;
class GraphReader : IDisposable {
    StreamReader xmlReader;
    public static string CreateFromFile(string fileName, out int settings) {
        if (fileName == null) {
            settings = 0;
            return null;
        }
        using (Stream stream = File.OpenRead(fileName)) {
            settings = stream.ReadByte();
            return ""ok"";
        }
    }
    protected virtual void Dispose(bool disposing) {
        if (disposing) xmlReader.Close();
    }
    public void Dispose() { Dispose(true); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should not have throws Exception — body is wrapped with try-catch
        Assert.DoesNotContain("throws Exception", code);
        // Method signature should be well-formed
        var cfLine = code.Split('\n').FirstOrDefault(l => l.Contains("createFromFile") && l.Contains("IntHolder"));
        _out.WriteLine($"createFromFile line: {cfLine}");
        if (cfLine != null)
            Assert.DoesNotContain("), ", cfLine);
    }

    [Fact]
    public async Task ProjectPipeline_MethodWithUsing_WrapsBody()
    {
        var source = @"
using System;
using System.IO;
class GeometryGraphReader : IDisposable {
    StreamReader xmlReader;
    public static string CreateFromFile(string fileName) {
        int settings;
        return CreateFromFile(fileName, out settings);
    }
    public static string CreateFromFile(string fileName, out int settings) {
        if (FirstCharacter(fileName) != '<') {
            settings = 0;
            return null;
        }
        using (Stream stream = File.OpenRead(fileName)) {
            settings = stream.ReadByte();
            return ""ok"";
        }
    }
    static char FirstCharacter(string fileName) {
        using (TextReader reader = File.OpenText(fileName)) {
            var first = (char)reader.Peek();
            return first;
        }
    }
    protected virtual void Dispose(bool disposing) {
        if (disposing) xmlReader.Close();
    }
    public void Dispose() { Dispose(true); }
}";
        var options = new ConversionOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var sourceFiles = new List<SourceFile>
        {
            new SourceFile { FilePath = "GeometryGraphReader.cs", Content = source }
        };
        var results = await pipeline.ConvertProjectAsync(sourceFiles,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFullPath("GeometryGraphReader.cs") });

        var result = results.FirstOrDefault(r => r.FileName?.Contains("GeometryGraphReader") == true);
        Assert.NotNull(result);
        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success);
        var code = result.GeneratedCode ?? "";

        // close methods should not have throws Exception
        Assert.DoesNotContain("void close(boolean disposing) throws Exception", code);
        Assert.DoesNotContain("void close() throws Exception", code);

        // Methods should not have throws Exception (bodies are wrapped)
        Assert.DoesNotContain("firstCharacter(String fileName) throws Exception", code);

        var cfLine = code.Split('\n').FirstOrDefault(l => l.Contains("createFromFile") && l.Contains("IntHolder"));
        _out.WriteLine($"createFromFile line: {cfLine}");
        if (cfLine != null)
        {
            Assert.DoesNotContain("throws Exception", cfLine);
            Assert.DoesNotContain("), ", cfLine);
        }
    }

    [Fact]
    public void SiblingCall_EachMethodWrapsOwnBody()
    {
        // With body wrapping, each method independently wraps its own checked-exception
        // throwing code. Sibling calls don't need propagation since wrapped methods
        // no longer throw checked exceptions.
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(javaLibrary, diagnostics);

        // Method B: contains try-with-resources → body will be wrapped
        var methodB = new JavaMethodDeclaration { Name = "createFromFile", ReturnType = "String" };
        methodB.StructuredBody = new JavaMethodBody();
        methodB.StructuredBody.Statements.Add(new JavaRawStatement(
            "try (InputStream stream = Files.newInputStream(path)) { return \"\"; }"));

        // Method A: wrapper, calls sibling overload, no try-with-resources
        var methodA = new JavaMethodDeclaration { Name = "createFromFile", ReturnType = "String" };
        methodA.StructuredBody = new JavaMethodBody();
        methodA.StructuredBody.Statements.Add(new JavaRawStatement(
            "return createFromFile(fileName, holder);"));

        var clazz = new JavaClassDeclaration { Name = "GeometryGraphReader" };
        clazz.Methods.Add(methodA);
        clazz.Methods.Add(methodB);

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        // Method B should be wrapped (has try-with-resources with IOException from InputStream.close())
        Assert.Empty(methodB.ThrownExceptions);
        Assert.IsType<JavaTryCatchStatement>(methodB.StructuredBody.Statements[0]);

        // Method A should NOT be wrapped (sibling call is already wrapped, no new checked exceptions)
        Assert.Empty(methodA.ThrownExceptions);
        Assert.IsNotType<JavaTryCatchStatement>(methodA.StructuredBody.Statements[0]);
    }

    [Fact]
    public void FullPipeline_WrapperCallingMethodWithUsing_WrapsOwnBody()
    {
        var pipeline = new ConversionPipeline();
        var r = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System;
using System.IO;
class GeometryReader {
    public static string CreateFromFile(string fileName) {
        return CreateFromFile(fileName, 0);
    }
    public static string CreateFromFile(string fileName, int settings) {
        using (Stream stream = File.OpenRead(fileName)) {
            return fileName;
        }
    }
}",
            FileName = "GeometryReader.cs",
            Options = new ConversionOptions { JavaMetadataPath = JavaConfigDir },
        });
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Full overload: has try-with-resources — should NOT have throws Exception
        var fullLine = code.Split('\n').FirstOrDefault(l =>
            l.Contains("createFromFile") && l.Contains("int settings"));
        _out.WriteLine($"Full overload line: {fullLine}");
        if (fullLine != null)
            Assert.DoesNotContain("throws Exception", fullLine!);

        // Wrapper: no try-with-resources — should NOT have throws Exception either
        var wrapperLine = code.Split('\n').FirstOrDefault(l =>
            l.Contains("createFromFile") && !l.Contains("int settings") &&
            (l.Contains("static String") || l.Contains("throws")));
        _out.WriteLine($"Wrapper line: {wrapperLine}");
        if (wrapperLine != null)
            Assert.DoesNotContain("throws Exception", wrapperLine!);
    }

    [Fact]
    public void FileOpenCreate_UsingStream_UsesStreamWrapperForWriteMode()
    {
        var r = Convert(@"
using System.IO;
class GeometryGraphWriter {
    public GeometryGraphWriter(Stream stream, object graph, object settings) { }
    public void Write() { }
}
class Sample {
    public static void Save(string fileName, object graph, object settings) {
        using (Stream stream = File.Open(fileName, FileMode.Create)) {
            var graphWriter = new GeometryGraphWriter(stream, graph, settings);
            graphWriter.Write();
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("try (StreamWrapper stream = FileHelper.open(fileName, FileMode.Create))", code);
        Assert.Contains("new GeometryGraphWriter(stream, graph, settings)", code);
        Assert.DoesNotContain("try (InputStream stream = FileHelper.open(fileName, FileMode.Create))", code);
    }
}
