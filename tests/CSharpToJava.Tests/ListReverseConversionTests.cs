using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class ListReverseConversionTests
{
    private static ConversionResult Convert(string csharpCode, ConversionOptions? options = null)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            Options = options ?? new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java25,
                UseRecords = true,
                PreferStreamApi = true,
            }
        });
    }

    private static string ConvertAndGetCode(string code, ConversionOptions? options = null)
    {
        var result = Convert(code, options);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    [Fact]
    public void ListReverse_InstanceMethod_UsesCollectionsReverse()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                public List<int> Build(List<int> values)
                {
                    values.Reverse();
                    return values;
                }
            }
            """;

        var java = ConvertAndGetCode(code);
        Assert.Contains("Collections.reverse(values)", java);
        Assert.DoesNotContain("values.reverse()", java);
    }
}
