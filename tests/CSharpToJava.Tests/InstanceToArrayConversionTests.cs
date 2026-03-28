using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class InstanceToArrayConversionTests
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
    public void ListOfReferenceType_ToArray_UsesTypedArrayFactory()
    {
        const string code = """
            using System.Collections.Generic;

            class Curve { }

            class C
            {
                public Curve[] Build(List<Curve> curves)
                {
                    return curves.ToArray();
                }
            }
            """;

        var java = ConvertAndGetCode(code);
        Assert.Contains("curves.toArray(Curve[]::new)", java);
    }
}
