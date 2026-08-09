using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Expression-bodied local functions must apply the same target-type adaptation as
/// regular expression-bodied methods. A local function returning ReadOnlySpan&lt;byte&gt;
/// from a byte[] initializer was emitted as a raw byte[] return, which javac rejects
/// ("byte[] cannot be converted to ReadOnlySpan&lt;Integer&gt;").
/// Regression: RegexCharClass.AsciiLookup() => new byte[] {...}.
/// </summary>
public class LocalFunctionExpressionBodySpanAdaptationTests
{
    [Fact]
    public void LocalFunctionExpressionBodyReturningReadOnlySpan_IsWrapped()
    {
        var result = Convert(@"
class CharClass
{
    public bool IsWordChar(int ch)
    {
        static System.ReadOnlySpan<byte> AsciiLookup() => new byte[]
        {
            0x00, 0x00, 0xFF, 0x03
        };
        System.ReadOnlySpan<byte> asciiLookup = AsciiLookup();
        return true;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new ReadOnlySpan<Integer>(", code);
        Assert.DoesNotContain("ReadOnlySpan<Integer> asciiLookup() { return new byte[]", code);
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
