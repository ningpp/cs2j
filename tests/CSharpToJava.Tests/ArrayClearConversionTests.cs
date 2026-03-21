using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayClearConversionTests
{
    [Fact]
    public void ArrayClear_WithReferenceArray_MapsToArraysFill()
    {
        const string code = """
            using System;

            public class C {
                public void M(string[] items) {
                    Array.Clear(items, 0, items.Length);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Arrays.fill(items", result.GeneratedCode);
        Assert.DoesNotContain("Object.clear", result.GeneratedCode);
    }
}
