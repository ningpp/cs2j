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
        Assert.Contains("return doubleDict.get(1.0);", result.GeneratedCode, StringComparison.Ordinal);
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
        Assert.Contains("return doubleDict.get((double) (key));", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_RewritesImplicitNumericConversions_ForGenericMethodArguments()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    float AddAndRead(int value)
    {
        var values = new List<float>();
        values.Add(value);
        return values[0];
    }
}");

        Assert.True(result.Success);
        Assert.Contains("values.add((float) (value));", result.GeneratedCode, StringComparison.Ordinal);
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
        Assert.Contains("Arrays.asList(1L, 2L)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("put(3L, 4L);", result.GeneratedCode, StringComparison.Ordinal);
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