using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

public class MultiIndexerAssignmentTests
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
    public void MultiArgIndexerAssignment_UsesSetNotPut()
    {
        const string code = """
            class M
            {
                public int this[int r, int c]
                {
                    get => 0;
                    set { }
                }

                public void Init()
                {
                    this[0, 0] = this[1, 1] = 1;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("this.set(1, 1, 1)", result.GeneratedCode);
        Assert.Contains("this.set(0, 0", result.GeneratedCode);
        Assert.DoesNotContain("this.put(0, 0", result.GeneratedCode);
    }

    [Fact]
    public void SortedDictionaryIndexerAssignment_UsesPut()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                void M()
                {
                    var dict = new SortedDictionary<double, string>();
                    dict[1.0] = "x";
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("dict.put(", result.GeneratedCode);
        Assert.DoesNotContain("dict.set(", result.GeneratedCode);
    }
}
