using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumeratorMoveNextConversionTests
{
    [Fact]
    public void EnumeratorMoveNext_UsesHasNext()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public bool M(Dictionary<int, int> d) {
                    var e = d.GetEnumerator();
                    return e.MoveNext();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("e.hasNext()", result.GeneratedCode);
        Assert.DoesNotContain("e.moveNext()", result.GeneratedCode);
    }
}
