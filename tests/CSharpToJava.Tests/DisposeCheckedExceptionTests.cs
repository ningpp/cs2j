using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.Pipeline;
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

    }
