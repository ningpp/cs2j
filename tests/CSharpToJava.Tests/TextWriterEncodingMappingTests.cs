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

    // ── TextWriter.Write(char[], int, int) tests ────────────────────

    /// <summary>
    /// TextWriter.Write(char[], int, int) is a subarray write, NOT a format call.
    /// It should map to writer.write(buf, off, len), not print(String.format(...)).
    /// Java's PrintWriter has no print(char[], int, int), but Writer.write() does.
    /// </summary>
    [Fact]
    public void TextWriter_Write_CharArray_MapsToWrite()
    {
        var result = Convert(@"
using System.IO;
class Test
{
    void Foo(TextWriter w, char[] buf)
    {
        w.Write(buf, 0, 10);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should use write, not print+String.format
        Assert.DoesNotContain("String.format", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("w.write(buf, 0, 10)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// TextWriter.Write(string, object[]) (format overload) should still use
    /// print(String.format(...)) — this is the correct format call pattern.
    /// </summary>
    [Fact]
    public void TextWriter_Write_StringFormat_StillUsesPrint()
    {
        var result = Convert(@"
using System.IO;
class Test
{
    void Foo(TextWriter w)
    {
        w.Write(""Hello {0}"", ""World"");
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Format overload should use print+String.format
        Assert.Contains("w.print(String.format(", result.GeneratedCode, StringComparison.Ordinal);
    }
}
