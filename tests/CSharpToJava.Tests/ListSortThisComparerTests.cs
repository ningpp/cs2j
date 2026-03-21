using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ListSortThisComparerTests
{
    [Fact]
    public void ListSort_WithThisComparer_UsesMethodReference()
    {
        const string code = """
            using System.Collections.Generic;

            public class C : IComparer<int> {
                public int Compare(int a, int b) => a.CompareTo(b);
                public void M(List<int> xs) {
                    xs.Sort(this);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("xs.sort(this::compare)", result.GeneratedCode);
    }
}
