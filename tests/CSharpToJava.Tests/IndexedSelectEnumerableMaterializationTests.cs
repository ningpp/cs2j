using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IndexedSelectEnumerableMaterializationTests
{
    [Fact]
    public void IndexedSelect_AsEnumerableArgument_MaterializesToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                static void Consume(IEnumerable<int> xs) { }

                public static void M(int[] arr) {
                    Consume(arr.Select((x, i) => x + i));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("collectingAndThen(Collectors.toList(),", result.GeneratedCode);
        Assert.Contains(".collect(Collectors.toList()))", result.GeneratedCode);
    }
}
