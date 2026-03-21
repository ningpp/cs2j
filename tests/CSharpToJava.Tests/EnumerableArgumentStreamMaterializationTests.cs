using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumerableArgumentStreamMaterializationTests
{
    [Fact]
    public void EnumerableParameter_WithConcatArgument_MaterializesStreamToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                static IEnumerable<int> F(IEnumerable<int> items) => items;

                static IEnumerable<int> G(IEnumerable<int> items, int x) {
                    return F(items.Concat(new[] { x }));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("collect(Collectors.toList())", result.GeneratedCode);
    }
}
