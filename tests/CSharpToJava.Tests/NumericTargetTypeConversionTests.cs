using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NumericTargetTypeConversionTests
{
    [Fact]
    public void ConversionPipeline_RewritesImplicitNumericConversions_ForDoubleDictionaryIndexer()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    double ReadWrite()
    {
        var doubleDict = new Dictionary<double, double>();
        doubleDict[1] = 2;
        return doubleDict[1];
    }
}");

        Assert.True(result.Success);
        Assert.Contains("doubleDict.put(1.0, 2.0);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return doubleDict.getChecked(1.0);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_RewritesImplicitNumericConversions_ForIndexerVariablesToo()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    double ReadWrite(int key, int value)
    {
        var doubleDict = new Dictionary<double, double>();
        doubleDict[key] = value;
        return doubleDict[key];
    }
}");

        Assert.True(result.Success);
        Assert.Contains("doubleDict.put((double) (key), (double) (value));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return doubleDict.getChecked((double) (key));", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_RewritesImplicitNumericConversions_ForNullableInitializersAndReturns()
    {
        var result = Convert(@"
class Sample
{
    double? Field = 1;

    double? Read()
    {
        double? local = 2;
        return 3;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Double Field = 1.0;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Double local = 2.0;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return 3.0;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_RewritesImplicitNumericConversions_ForCollectionInitializers()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        var list = new List<long> { 1, 2 };
        var dict = new Dictionary<long, long> { { 3, 4 } };
    }
}");

        Assert.True(result.Success);
        Assert.Contains("ArrayHelper.toList(new Long[] { 1L, 2L })", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("put(3L, 4L);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_NegativeZeroInt_ToDouble_ProducesPositiveZero()
    {
        // C# integer -0 is just 0; when assigned to double it becomes 0.0 (positive zero).
        // The converter must NOT produce -0.0 (double negative zero) because
        // XmlConvert.ToString(-0.0) returns "-0" in C# semantics.
        var result = Convert(@"
class Sample
{
    double Test()
    {
        double d = -0;
        return d;
    }
}");

        Assert.True(result.Success);
        // Should be 0.0, not -0.0
        Assert.Contains("double d = 0.0", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("-0.0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_NegativeZeroInt_ToFloat_ProducesPositiveZero()
    {
        var result = Convert(@"
class Sample
{
    float Test()
    {
        float f = -0;
        return f;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("float f = 0.0f", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("-0.0f", result.GeneratedCode, StringComparison.Ordinal);
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