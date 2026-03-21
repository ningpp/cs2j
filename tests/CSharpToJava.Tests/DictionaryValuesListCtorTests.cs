using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DictionaryValuesListCtorTests
{
    [Fact]
    public void DictionaryValues_PassedToListConstructor_DoesNotForceIterableCast()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                public List<int> M(Dictionary<string, int> d) {
                    return new List<int>(d.Values);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new ArrayList<", result.GeneratedCode);
        Assert.Contains("d.values()", result.GeneratedCode);
        Assert.DoesNotContain("(Iterable<Integer>)(Iterable<?>)(d.values())", result.GeneratedCode.Replace(" ", string.Empty));
    }
}
