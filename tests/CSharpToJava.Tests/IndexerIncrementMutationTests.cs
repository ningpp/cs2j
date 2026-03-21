using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IndexerIncrementMutationTests
{
    [Fact]
    public void DictionaryIndexerIncrement_UsesPutWithGetPlusOne()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                void M(Dictionary<int, int> d) {
                    d[1]++;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("d.put(1, d.get(1) + 1)", result.GeneratedCode);
        Assert.DoesNotContain("d.get(1)++", result.GeneratedCode);
    }
}
