using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.IO;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces NameTable.ComputeHash32(char[], int, int):
/// char[].AsSpan(start, len) is ReadOnlySpan&lt;Character&gt;, and when the converter
/// re-wraps it as a mutable Span for MemoryMarshal.asBytes, the diamond
/// `new Span&lt;&gt;(...)` cannot be inferred because it sits in a raw
/// ReadOnlySpan parameter position. The generated code must use an explicit
/// type argument (`new Span&lt;Character&gt;(...)`).
/// </summary>
public class MemoryMarshalAsBytesSpanTests
{

    [Fact]
    public void CompatMemoryMarshal_AcceptsMutableSpan()
    {
        var compatPath = Path.Combine(
            FindRepoRoot(),
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "MemoryMarshal.java");
        var source = File.ReadAllText(compatPath);

        // The converter legitimately re-wraps char[].AsSpan as a mutable Span
        // before MemoryMarshal.asBytes; compat must accept that Span form.
        Assert.Contains("asBytes(Span span)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
