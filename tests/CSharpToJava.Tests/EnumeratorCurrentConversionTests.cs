using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumeratorCurrentConversionTests
{
    [Fact]
    public void IEnumeratorCurrent_MapsToIteratorNext()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public void M(List<int> items) {
                    IEnumerator<int> e = items.GetEnumerator();
                    while (e.MoveNext()) {
                        var x = e.Current;
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("e.hasNext()", result.GeneratedCode);
        Assert.Contains("e.next()", result.GeneratedCode);
        Assert.DoesNotContain("e.getCurrent()", result.GeneratedCode);
    }
}
