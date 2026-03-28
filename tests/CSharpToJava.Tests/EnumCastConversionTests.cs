using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class EnumCastConversionTests
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
    public void NumericCastToEnum_UsesValuesIndex()
    {
        const string code = """
            class C
            {
                enum V { A = 0, B = 1, Cc = 2 }

                V Get(int i)
                {
                    return (V)i;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("V.values()[(int)(i)]", result.GeneratedCode);
    }
}
