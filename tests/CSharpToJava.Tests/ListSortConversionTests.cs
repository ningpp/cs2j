using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class ListSortConversionTests
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
    public void ListSort_NoComparer_UsesCollectionsSort()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                void M(List<int> values)
                {
                    values.Sort();
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Collections.sort(values)", result.GeneratedCode);
        Assert.DoesNotContain("values.sort()", result.GeneratedCode);
    }
}
