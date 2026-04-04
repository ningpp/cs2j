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
/// C# IDisposable.Dispose() maps to Java AutoCloseable.close(), which declares
/// <c>throws Exception</c>. The converted Java methods must carry the throws clause.
/// Also validates that try-with-resources from C# <c>using</c> statements adds
/// appropriate checked-exception throws to the enclosing method.
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
    public void DisposeBool_HasThrowsException()
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
        // Both close() and close(boolean) should declare throws Exception
        Assert.Contains("void close(boolean disposing) throws Exception", code);
        Assert.Contains("void close() throws Exception", code);
    }

    [Fact]
    public void DisposeNoArgs_HasThrowsException()
    {
        var r = Convert(@"
using System;
class Simple : IDisposable {
    public void Dispose() { }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("void close() throws Exception", code);
    }

    [Fact]
    public void TryWithResources_InputStream_AddsThrowsIOException()
    {
        // Test the rewriter directly: a method with a raw try-with-resources on InputStream
        // should get IOException added to its throws clause.
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

        _out.WriteLine("ThrownExceptions: " + string.Join(", ", method.ThrownExceptions));
        Assert.Contains("IOException", method.ThrownExceptions);
    }

    [Fact]
    public void TryWithResources_UnknownType_AddsThrowsException()
    {
        // For resource types not in the JDK index, fall back to Exception
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

        _out.WriteLine("ThrownExceptions: " + string.Join(", ", method.ThrownExceptions));
        Assert.Contains("Exception", method.ThrownExceptions);
    }

    [Fact]
    public void FullPipeline_StaticMethodWithUsing_GetsThrowsException()
    {
        // Test that the full pipeline with Java library metadata adds throws
        // to a static method containing a using statement
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
        // Check that close methods get throws Exception
        Assert.Contains("void close(boolean disposing) throws Exception", code);
        // Check firstCharacter: in the simple pipeline (no Java library), 
        // the rewriter won't detect try-with-resources exceptions.
        // Just verify the Dispose→close fix works.
        _out.WriteLine("--- firstCharacter section ---");
        var fcIdx = code.IndexOf("firstCharacter");
        if (fcIdx >= 0)
        {
            var snippet = code.Substring(Math.Max(0, fcIdx - 20), Math.Min(200, code.Length - Math.Max(0, fcIdx - 20)));
            _out.WriteLine(snippet);
        }
        // Phase 4: UsingStatementSyntax detection should add throws Exception
        Assert.Contains("firstCharacter(String fileName) throws Exception", code);
    }

    [Fact]
    public void OutParamMethodWithUsing_GetsThrowsException()
    {
        // A method with an out parameter and a using statement should get throws Exception
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
        // Method with out param + using should have throws Exception
        Assert.Contains("throws Exception", code);
        // Check the method signature is well-formed (no dangling comma before exception)
        Assert.DoesNotContain("), ", code.Split('\n').FirstOrDefault(l => l.Contains("createFromFile")) ?? "");
    }

    [Fact]
    public async Task ProjectPipeline_MethodWithUsing_GetsThrowsException()
    {
        // Test with the project conversion pipeline (not single-file)
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

        // All close methods should have throws Exception
        Assert.Contains("void close(boolean disposing) throws Exception", code);
        Assert.Contains("void close() throws Exception", code);

        // FirstCharacter has using statement → should have throws Exception
        Assert.Contains("firstCharacter(String fileName) throws Exception", code);

        // CreateFromFile(String, out int) has using statement → should have throws Exception
        var cfLine = code.Split('\n').FirstOrDefault(l => l.Contains("createFromFile") && l.Contains("IntHolder"));
        _out.WriteLine($"createFromFile line: {cfLine}");
        Assert.NotNull(cfLine);
        Assert.Contains("throws Exception", cfLine);
        // Signature must be well-formed (no dangling comma)
        Assert.DoesNotContain("), ", cfLine);
    }

    [Fact]
    public void SiblingCall_ThrowsPropagatedToCaller_ViaRewriter()
    {
        // Unit test for the two-pass sibling-throws propagation in JavaExceptionCheckRewriter.
        // Method A (wrapper) calls method B (sibling). B already has throws Exception.
        // After the rewriter runs, A should also declare throws Exception.
        var javaLibrary = new JavaLibraryIndex(JavaConfigDir);
        var diagnostics = new DiagnosticCollector();
        var rewriter = new JavaExceptionCheckRewriter(javaLibrary, diagnostics);

        // Method B: createFromFile(String, ObjectHolder) — already has throws Exception
        // (simulating MethodTransformer's using-statement detection output)
        var methodB = new JavaMethodDeclaration { Name = "createFromFile", ReturnType = "String" };
        methodB.ThrownExceptions.Add("Exception");
        methodB.StructuredBody = new JavaMethodBody();
        methodB.StructuredBody.Statements.Add(new JavaRawStatement(
            "try (InputStream stream = Files.newInputStream(path)) { return \"\"; }"));

        // Method A: createFromFile(String) — wrapper, calls sibling overload, NO using statement
        var methodA = new JavaMethodDeclaration { Name = "createFromFile", ReturnType = "String" };
        methodA.StructuredBody = new JavaMethodBody();
        methodA.StructuredBody.Statements.Add(new JavaRawStatement(
            "return createFromFile(fileName, holder);"));

        var clazz = new JavaClassDeclaration { Name = "GeometryGraphReader" };
        clazz.Methods.Add(methodA); // wrapper first
        clazz.Methods.Add(methodB); // full overload second

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(clazz);

        rewriter.VisitCompilationUnit(cu);

        _out.WriteLine("Method A (wrapper) throws: " + string.Join(", ", methodA.ThrownExceptions));
        _out.WriteLine("Method B (full)    throws: " + string.Join(", ", methodB.ThrownExceptions));

        Assert.Contains("Exception", methodB.ThrownExceptions);
        // KEY assertion: wrapper should have Exception propagated from sibling call
        Assert.Contains("Exception", methodA.ThrownExceptions);
    }

    [Fact]
    public void FullPipeline_WrapperCallingMethodWithUsing_GetsThrowsException()
    {
        // End-to-end: a wrapper method that has no using statement itself, but calls
        // a sibling overload that does, should get throws Exception in Java output.
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

        // Full overload: has using statement → MethodTransformer adds throws Exception
        var fullLine = code.Split('\n').FirstOrDefault(l =>
            l.Contains("createFromFile") && l.Contains("int settings"));
        _out.WriteLine($"Full overload line: {fullLine}");
        Assert.NotNull(fullLine);
        Assert.Contains("throws Exception", fullLine!);

        // Wrapper: calls the sibling → JavaExceptionCheckRewriter should propagate throws
        var wrapperLine = code.Split('\n').FirstOrDefault(l =>
            l.Contains("createFromFile") && !l.Contains("int settings") &&
            (l.Contains("static String") || l.Contains("throws")));
        _out.WriteLine($"Wrapper line: {wrapperLine}");
        Assert.NotNull(wrapperLine);
        Assert.Contains("throws Exception", wrapperLine!);
    }
}
