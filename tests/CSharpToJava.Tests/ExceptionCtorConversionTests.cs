using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class ExceptionCtorConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            Options = new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java21,
                UseRecords = true,
                PreferStreamApi = true,
            }
        });
    }

    [Fact]
    public void ArgumentOutOfRangeException_TwoStringArgs_MapsToSingleIllegalArgumentMessage()
    {
        const string code = """
            class C
            {
                void M(double value)
                {
                    if (value <= 0)
                        throw new System.ArgumentOutOfRangeException("value", "must be positive");
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("new IllegalArgumentException(\"value\" + \": \" + \"must be positive\")", result.GeneratedCode);
        Assert.DoesNotContain("new IllegalArgumentException(\"value\",", result.GeneratedCode);
    }
}
