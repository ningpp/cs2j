using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class RepeatedOutCallHolderTests
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
    public void RepeatedOutCalls_DoNotReuseSameHolderLocalName()
    {
        const string code = """
            class C
            {
                bool TryGet(out int x)
                {
                    x = 1;
                    return true;
                }

                void M()
                {
                    int v;
                    TryGet(out v);
                    TryGet(out v);
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("_vHolder1", result.GeneratedCode);
        Assert.Contains("_vHolder2", result.GeneratedCode);
    }
}
