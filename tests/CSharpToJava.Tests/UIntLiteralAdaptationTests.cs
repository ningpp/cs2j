using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UIntLiteralAdaptationTests
{
    [Fact]
    public void UIntLiteralBeyondIntMax_OnIntArray_EmitsWrappedCast()
    {
        var result = Convert(@"
public class Sample
{
    public static void M()
    {
        var uints = new uint[10];
        uints[2] = 3294967295;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(int) 3294967295L", code, StringComparison.Ordinal);
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
