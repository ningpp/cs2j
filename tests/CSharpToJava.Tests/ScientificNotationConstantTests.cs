using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ScientificNotationConstantTests
{
    [Fact]
    public void DoubleConstScientificNotation_DoesNotAppendDecimalZero()
    {
        var result = Convert(@"
public class Sample
{
    public const double ClusterDefaultFreeWeight = 1e-6;
    public const double EventComparisonEpsilon = 1e-6;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("1E-06.0", code, StringComparison.Ordinal);
        Assert.DoesNotContain("1e-06.0", code, StringComparison.Ordinal);
        Assert.Contains("1E-06", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleConstWithDecimalPoint_KeepsLiteralUnchanged()
    {
        var result = Convert(@"
public class Sample
{
    public const double ClusterDefaultFixedWeight = 100000000.0;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("100000000.0", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
