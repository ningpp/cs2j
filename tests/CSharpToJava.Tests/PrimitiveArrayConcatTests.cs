using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PrimitiveArrayConcatTests
{
    [Fact]
    public void Concat_OnPrimitiveArrays_BoxesLeftStream()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                IEnumerable<int> M(int[] a, int[] b) {
                    return a.Concat(b);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Stream.concat(Arrays.stream(a).boxed(),", result.GeneratedCode);
    }
}
