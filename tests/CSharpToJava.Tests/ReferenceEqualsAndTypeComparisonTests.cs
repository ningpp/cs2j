using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class ReferenceEqualsAndTypeComparisonTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ReferenceEqualsAndGetTypeComparison_UseJavaOperators()
    {
        const string code = """
            class C
            {
                public override bool Equals(object obj)
                {
                    if (ReferenceEquals(null, obj)) return false;
                    if (ReferenceEquals(this, obj)) return true;
                    if (obj.GetType() != GetType()) return false;
                    return true;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("(null == obj)", result.GeneratedCode);
        Assert.Contains("(this == obj)", result.GeneratedCode);
        Assert.Contains("obj.getClass() != getClass()", result.GeneratedCode);
        Assert.DoesNotContain("referenceEquals(", result.GeneratedCode);
        Assert.DoesNotContain("Class.notEquals", result.GeneratedCode);
    }
}
