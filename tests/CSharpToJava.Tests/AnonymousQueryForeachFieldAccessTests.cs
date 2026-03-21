using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class AnonymousQueryForeachFieldAccessTests
{
    [Fact]
    public void AnonymousQueryForeach_WithPairFieldAccess_RewritesWithoutPairOrSelfAlias()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public void M(List<int> vertices) {
                    foreach (var pair in from source in vertices
                                         from target in vertices
                                         select new { source, target }) {
                        var source = pair.source;
                        var target = pair.target();
                        _ = source + target;
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("pair.source", result.GeneratedCode);
        Assert.DoesNotContain("pair.target", result.GeneratedCode);
        Assert.DoesNotContain("var source = source;", result.GeneratedCode);
        Assert.DoesNotContain("var target = target;", result.GeneratedCode);
    }
}
