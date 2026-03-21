using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ForeachCollectedListVariableTests
{
    [Fact]
    public void Foreach_OverCollectedLocalList_DoesNotAppendCollectAgain()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public int Sum(IEnumerable<int> items) {
                    var neighb = items.OrderBy(x => x).ToList();
                    int sum = 0;
                    foreach (var t in neighb) {
                        sum += t;
                    }
                    return sum;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("neighb.collect(", result.GeneratedCode);
        Assert.Contains(": neighb)", result.GeneratedCode);
    }
}
