using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class StringFormatConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void StringFormat_StaticCall_MapsToJavaStringFormat()
    {
        const string code = """
            class C
            {
                string M(double t, double par, int idx, double u)
                {
                    return string.Format("Check, args t:{0}, par:{1}, segIndex:{2} and u:{3}", t, par, idx, u);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("String.format(", result.GeneratedCode);
        Assert.DoesNotContain("String.Format(", result.GeneratedCode);
    }
}
