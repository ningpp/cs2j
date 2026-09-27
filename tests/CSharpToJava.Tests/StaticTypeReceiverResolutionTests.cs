using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StaticTypeReceiverResolutionTests
{

    [Fact]
    public void ConversionPipeline_DoesNotRewriteFormatString_ForCustomStringTypes()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Text
{
    public static class String
    {
        public static string Format(string pattern, object value) => pattern + value;
    }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public string Build(string joined)
        {
            return Text.String.Format(""{0}"", joined);
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("String.format(\"{0}\", joined)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.format(\"%s\", joined)", result.GeneratedCode, StringComparison.Ordinal);
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
