using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class ConditionalEnumerableTernaryTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void TernaryWhereExpressionReturningIEnumerable_CollectsBothBranches()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                IEnumerable<int> Pick(List<int> a, List<int> b, bool flag)
                {
                    return flag ? a.Where(x => x > 0) : b.Where(x => x > 0);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("collect(Collectors.toList())", result.GeneratedCode);
    }
}
