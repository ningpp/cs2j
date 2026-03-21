using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class GenericNewConstraintTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void NewOnCollectionConstrainedTypeParameter_DoesNotEmitNewTypeParameter()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
                    where TC : ICollection<TS>, new()
                {
                    TC tc;
                    if (!dictionary.TryGetValue(key, out tc))
                        dictionary[key] = tc = new TC();

                    tc.Add(value);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("new TC()", result.GeneratedCode);
        Assert.Contains("(TC) new ArrayList<>()", result.GeneratedCode);
    }
}
