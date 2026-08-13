using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SpanToArrayExtensionTests
{
    [Fact]
    public void SpanByteToArray_ExtensionForm_UsesToByteArray()
    {
        var result = Convert(@"
public class Sample
{
    public static byte[] M(byte[] src, int len)
    {
        return src.AsSpan(0, len).ToArray();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("asSpan(src, 0, len).toByteArray()", code, StringComparison.Ordinal);
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
