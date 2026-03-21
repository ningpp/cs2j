using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArraySortKeysItemsStatementTests
{
    [Fact]
    public void ArraySort_WithKeysAndItems_ReordersItemsAlongKeys()
    {
        const string code = """
            using System;

            public class C {
                void M(double[] keys, string[] items) {
                    Array.Sort(keys, items);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("IntStream.range", result.GeneratedCode);
        Assert.Contains("items[i] =", result.GeneratedCode);
        Assert.DoesNotContain("Arrays.sort(keys, items)", result.GeneratedCode);
    }
}
