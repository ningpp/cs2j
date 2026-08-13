using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class Utf8ParserTryParseTests
{
    [Fact]
    public void TryParseDecimal_MapsToTryParseDecimal()
    {
        var result = Convert(@"
using System.Buffers.Text;

public class Sample
{
    public static bool M(System.ReadOnlySpan<byte> span, out decimal value, out int bytesConsumed, char format)
    {
        return Utf8Parser.TryParse(span, out value, out bytesConsumed, format);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("tryParseDecimal(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseGuid_MapsToTryParseUuid()
    {
        var result = Convert(@"
using System.Buffers.Text;

public class Sample
{
    public static bool M(System.ReadOnlySpan<byte> span, out System.Guid value, out int bytesConsumed)
    {
        return Utf8Parser.TryParse(span, out value, out bytesConsumed, 'D');
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("tryParseUuid(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseInt_StaysTryParse()
    {
        var result = Convert(@"
using System.Buffers.Text;

public class Sample
{
    public static bool M(System.ReadOnlySpan<byte> span, out int value, out int bytesConsumed)
    {
        return Utf8Parser.TryParse(span, out value, out bytesConsumed);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("tryParse(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("tryParseDecimal", code, StringComparison.Ordinal);
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
