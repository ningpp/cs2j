using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that TextWriter.Encoding property access generates code that
/// works with Java's PrintWriter (which lacks getEncoding()).
/// </summary>
public class TextWriterEncodingMappingTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    /// <summary>
    /// C# TextWriter.Encoding should generate a static helper call
    /// (TextWriterHelper.getEncoding(w)) instead of w.getEncoding(),
    /// because Java's PrintWriter lacks a getEncoding() method.
    /// </summary>
    [Fact]
    public void TextWriter_Encoding_UsesStaticHelper()
    {
        var result = Convert(@"
using System.IO;
class Test
{
    void Foo(TextWriter w)
    {
        var enc = w.Encoding;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should use static helper, not instance method
        Assert.DoesNotContain("w.getEncoding()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("TextWriterHelper.getEncoding(w)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// StreamWriter's own getEncoding() should still work for StreamWriter-typed variables.
    /// </summary>
    [Fact]
    public void StreamWriter_Encoding_UsesInstanceMethod()
    {
        var result = Convert(@"
using System.IO;
class Test
{
    void Foo(StreamWriter sw)
    {
        var enc = sw.Encoding;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // StreamWriter has getEncoding() directly
        Assert.Contains("sw.getEncoding()", result.GeneratedCode, StringComparison.Ordinal);
    }
}
