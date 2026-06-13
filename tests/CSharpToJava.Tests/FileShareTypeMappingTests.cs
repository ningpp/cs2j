using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.IO.FileShare enum maps correctly to io.github.ningpp.compat.FileShare.
/// </summary>
public class FileShareTypeMappingTests
{
    [Fact]
    public void FileShareRead_IsMappedTo_CompatFileShare()
    {
        var result = Convert(@"
using System.IO;
class Sample
{
    void M(string path)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 0x1000, false);
    }
}");

        System.Console.WriteLine("=== GENERATED CODE ===");
        System.Console.WriteLine(result.GeneratedCode);
        System.Console.WriteLine("=== END ===");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should NOT have the default namespace mapping
        Assert.DoesNotContain("dotnet.system.IO.FileShare", result.GeneratedCode, StringComparison.Ordinal);
        // Should have the correct compat library import
        Assert.Contains("io.github.ningpp.compat.FileShare", result.GeneratedCode, StringComparison.Ordinal);
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
