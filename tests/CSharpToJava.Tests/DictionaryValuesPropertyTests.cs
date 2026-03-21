using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class DictionaryValuesPropertyTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void SortedDictionaryValues_MapsToJavaValuesMethod()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                IEnumerable<int> M()
                {
                    var dict = new SortedDictionary<int, int>();
                    return dict.Values;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("values()", result.GeneratedCode);
        Assert.DoesNotContain("dict.getValues()", result.GeneratedCode);
    }
}
