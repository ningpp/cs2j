using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.ReadOnlyMemory&lt;T&gt; maps to the compat ReadOnlyMemory class
/// with the proper import, so generated code compiles against the compat library.
/// Reproduces SegmentStringBuilder/Capture: javac "cannot find symbol: class ReadOnlyMemory".
/// </summary>
public class ReadOnlyMemoryMappingTests
{
    [Fact]
    public void ReadOnlyMemoryOfChar_MapsToCompatType_WithImport()
    {
        var result = Convert(@"
class Holder {
    private System.ReadOnlyMemory<char> _segment;
    public System.ReadOnlyMemory<char> Segment => _segment;
    public System.ReadOnlyMemory<char> Sub(string s) => s.AsMemory(1, 2);
    public int Len(System.ReadOnlyMemory<char> m) => m.Length;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ReadOnlyMemory<Character>", code);
        Assert.Contains("import io.github.ningpp.compat.ReadOnlyMemory", code);
    }

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
}
