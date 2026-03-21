using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DictionaryGetEnumeratorConversionTests
{
    [Fact]
    public void DictionaryGetEnumerator_UsesEntrySetIterator()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public void M(Dictionary<int, int> d) {
                    var e = d.GetEnumerator();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("d.entrySet().iterator()", result.GeneratedCode);
        Assert.DoesNotContain("d.iterator()", result.GeneratedCode);
    }
}
