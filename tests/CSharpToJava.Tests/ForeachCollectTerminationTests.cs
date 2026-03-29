using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ForeachCollectTerminationTests
{
    [Fact]
    public void Foreach_StreamChainWithCollect_DoesNotDoubleCollect()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class V { public IEnumerable<int> Items { get; set; } = new List<int>(); }
            public class C {
                public int M(IEnumerable<V> vals) {
                    int sum = 0;
                    foreach (var c in vals.SelectMany(v => v.Items).Where(x => x > 0).ToList()) {
                        sum += c;
                    }
                    return sum;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(".collect(Collectors.toList()).collect(", result.GeneratedCode);
        Assert.DoesNotContain(".collect(Collectors.toList()).collect(", result.GeneratedCode);
    }
}
