using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ExplicitIEnumerableForeachMaterializationTests
{
    [Fact]
    public void ExplicitIEnumerable_WithOrderByThenBy_ForeachMaterializesStream()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public int Sum(IEnumerable<int> items) {
                    IEnumerable<int> ordered = items.OrderBy(x => Math.Abs(x)).ThenBy(x => x);
                    int total = 0;
                    foreach (var v in ordered) {
                        total += v;
                    }
                    return total;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("var ordered = StreamSupport.stream(items.spliterator(), false).sorted", result.GeneratedCode);
        Assert.Contains(".collect(Collectors.toList());", result.GeneratedCode);
        Assert.Contains(": ordered)", result.GeneratedCode);
    }
}
