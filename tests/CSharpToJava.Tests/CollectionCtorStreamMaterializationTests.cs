using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CollectionCtorStreamMaterializationTests
{
    [Fact]
    public void ListCtor_FromOrderByMaterializesStreamArgument()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                List<int> M(List<int> xs) {
                    return new List<int>(xs.OrderBy(x => x));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new ArrayList", result.GeneratedCode);
        Assert.Contains(".collect(Collectors.toList())", result.GeneratedCode);
    }
}
