using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArraySortConversionTests
{
    [Fact]
    public void ArraySort_WithComparer_UsesArraysSort()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            public class C {
                class Desc : IComparer<int> {
                    public int Compare(int x, int y) => y.CompareTo(x);
                }

                public void M() {
                    int[] values = new int[] { 3, 1, 2 };
                    Array.Sort(values, new Desc());
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Arrays.sort(values", result.GeneratedCode);
        Assert.DoesNotContain("Object.sort(values", result.GeneratedCode);
    }
}
