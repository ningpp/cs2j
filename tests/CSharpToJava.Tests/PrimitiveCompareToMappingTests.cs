using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# primitive numeric CompareTo calls map to the correct Java
/// static comparison helper. double.CompareTo(double) must become
/// Double.compare(double, double), not Integer.compare(int, int).
/// </summary>
public class PrimitiveCompareToMappingTests
{
    [Fact]
    public void DoubleCompareTo_UsesDoubleCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(double x, double y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Double.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatCompareTo_UsesFloatCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(float x, float y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Float.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntCompareTo_UsesIntegerCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(int x, int y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LongCompareTo_UsesLongCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(long x, long y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Long.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
