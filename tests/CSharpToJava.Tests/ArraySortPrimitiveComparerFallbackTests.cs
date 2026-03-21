using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArraySortPrimitiveComparerFallbackTests
{
    [Fact]
    public void ArraySort_PrimitiveArrayWithComparer_DropsComparerOverload()
    {
        const string code = """
            using System;

            public class C {
                int CompareByX(int a, int b) => a.CompareTo(b);

                void M(int[] p) {
                    Array.Sort(p, CompareByX);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Arrays.sort(p)", result.GeneratedCode);
        Assert.DoesNotContain("Arrays.sort(p,", result.GeneratedCode);
    }
}
