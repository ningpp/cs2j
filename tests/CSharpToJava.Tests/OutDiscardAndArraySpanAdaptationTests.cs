using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests two gaps exposed by HexConverter:
/// 1. out-discard arguments (out _) must use the typed holder (IntHolder for
///    out int), not new Object[1], so the typed overload accepts them.
/// 2. A byte[] produced where a ReadOnlySpan&lt;byte&gt; is expected (expression
///    body / return) must be wrapped in new ReadOnlySpan&lt;&gt;(...).
/// </summary>
public class OutDiscardAndArraySpanAdaptationTests
{
    [Fact]
    public void OutDiscard_UsesTypedHolder()
    {
        var result = Convert(@"
class OutDiscard
{
    static bool TryIt(int x, out int y)
    {
        y = x;
        return true;
    }

    static bool Wrap(int x)
    {
        return TryIt(x, out _);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("IntHolder()", code);
        Assert.DoesNotContain("new Object[1]", code);
    }

    [Fact]
    public void ByteArrayReturnedAsReadOnlySpan_IsWrapped()
    {
        var result = Convert(@"
class Lookup
{
    static System.ReadOnlySpan<byte> Table => new byte[] { 0xFF, 0x0, 0x1 };
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new ReadOnlySpan<Integer>(", code);
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
