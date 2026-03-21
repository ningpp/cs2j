using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class CollectionCopyToConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void HashSetCopyTo_UsesSystemArraycopy()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                void M()
                {
                    var hs = new HashSet<string>();
                    var arr = new string[10];
                    hs.CopyTo(arr, 1);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("System.arraycopy(hs.toArray(), 0, arr, 1, hs.size())", result.GeneratedCode);
        Assert.DoesNotContain("hs.copyTo", result.GeneratedCode);
    }
}
