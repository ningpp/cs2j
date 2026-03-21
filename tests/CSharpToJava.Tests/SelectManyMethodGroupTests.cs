using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SelectManyMethodGroupTests
{
    [Fact]
    public void SelectMany_MethodGroupReturningEnumerable_IsStreamWrapped()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                IEnumerable<int> F(int x) { yield return x; }
                IEnumerable<int> M(IEnumerable<int> xs) {
                    return xs.SelectMany(F);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(".flatMap(_sm -> StreamSupport.stream(this.f(_sm).spliterator(), false))", result.GeneratedCode);
    }
}
