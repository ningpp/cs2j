using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CreateRectangleNodeOnDataRangeTests
{
    [Fact]
    public void CreateRectangleNodeOnData_WithEnumerableRange_MaterializesFirstArgument()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class RectangleNode {
                public static int CreateRectangleNodeOnData(IEnumerable<int> data, Func<int, int> selector) => 0;
            }

            public class C {
                int M(List<int> xs) {
                    return RectangleNode.CreateRectangleNodeOnData(Enumerable.Range(0, xs.Count), i => xs[i]);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("createRectangleNodeOnData", result.GeneratedCode);
        Assert.Contains("collect(Collectors.toList())", result.GeneratedCode);
    }
}
