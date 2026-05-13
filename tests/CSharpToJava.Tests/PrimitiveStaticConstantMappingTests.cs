using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PrimitiveStaticConstantMappingTests
{
    [Fact]
    public void BoxedDoubleNaN_InStaticPropertyGetter_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public static double NoFixedPosition
    {
        get { return Double.NaN; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveDoubleNaN_UsesJavaWrapperConstant()
    {
        var result = Convert(@"
public class Sample
{
    public double GetNaN()
    {
        return double.NaN;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return Double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return double.NaN;", result.GeneratedCode, StringComparison.Ordinal);
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
