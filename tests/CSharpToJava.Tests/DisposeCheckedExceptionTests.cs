using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping.JavaModel;
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
}
