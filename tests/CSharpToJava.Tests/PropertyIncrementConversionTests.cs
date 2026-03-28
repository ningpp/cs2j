using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class PropertyIncrementConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            Options = new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java25,
                UseRecords = true,
                PreferStreamApi = true,
            }
        });
    }

    [Fact]
    public void PrefixIncrement_PropertyStatement_UsesSetterGetterRewrite()
    {
        const string code = """
            class Counter
            {
                public int Count { get; set; }

                public void Tick()
                {
                    ++this.Count;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("this.setCount(this.getCount() + 1)", result.GeneratedCode);
        Assert.DoesNotContain("++this.getCount()", result.GeneratedCode);
    }
}
