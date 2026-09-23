using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class Utf8ParserTryParseTests
{

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
