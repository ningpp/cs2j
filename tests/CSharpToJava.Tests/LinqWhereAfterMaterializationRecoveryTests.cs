using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LinqWhereAfterMaterializationRecoveryTests
{
    [Fact]
    public void QueryContinuation_DoesNotCallFilterOnCollectedList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public List<int> M(IEnumerable<int> xs) {
                    var q = from x in xs
                            select x
                            into v
                            where v > 0
                            select v;
                    return q.ToList();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("collect(java.util.stream.Collectors.toList()).filter(", result.GeneratedCode);
        Assert.DoesNotContain("collect(Collectors.toList()).filter(", result.GeneratedCode);
        Assert.Contains(".filter(", result.GeneratedCode);
    }
}
