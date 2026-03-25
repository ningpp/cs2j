using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ListRemoveRangeConversionTests
{
    [Fact]
    public void RemoveRange_ConvertedTo_SubListClear()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public void M() {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    list.RemoveRange(2, 3);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(".subList(2, 2 + 3).clear()", result.GeneratedCode);
        Assert.DoesNotContain(".subList(2, 3);", result.GeneratedCode);
    }

    [Fact]
    public void RemoveRange_WithExpressionArgs_ConvertedCorrectly()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public void M() {
                    var list = new List<string>();
                    int start = 1;
                    list.RemoveRange(start + 1, list.Count - start - 1);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(".subList(", result.GeneratedCode);
        Assert.Contains(").clear()", result.GeneratedCode);
    }
}
