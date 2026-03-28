using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class EnumOrdinalArgumentTests
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
    public void IntArgument_ForEnumParameter_UsesEnumValuesIndex()
    {
        const string code = """
            class C
            {
                enum VertexId { Corner = 0, A = 1 }

                int F(VertexId id) => 1;

                int Test()
                {
                    return F(0);
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("values()[(int)(0)]", result.GeneratedCode);
    }
}
