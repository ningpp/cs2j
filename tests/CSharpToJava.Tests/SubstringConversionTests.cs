using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SubstringConversionTests
{
    [Fact]
    public void Substring_OneArg_ConvertsDirectly()
    {
        var result = Convert(@"
class Sample
{
    public string GetSuffix(string s)
    {
        return s.Substring(2);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("s.substring(2)", result.GeneratedCode, StringComparison.Ordinal);
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
