using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class ListCapacityConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ListCapacity_GetterAndSetter_MapToJavaCompatibleCalls()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                void M()
                {
                    var segs = new List<int>();
                    int current = segs.Capacity;
                    segs.Capacity = current + 4;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("segs.size()", result.GeneratedCode);
        Assert.Contains("segs.ensureCapacity(current + 4)", result.GeneratedCode);
        Assert.DoesNotContain("getCapacity()", result.GeneratedCode);
        Assert.DoesNotContain("setCapacity(", result.GeneratedCode);
    }
}
