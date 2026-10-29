using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C# implicitly widens an integral expression to double at every target slot,
/// including reference-typed slots: <c>ByRef&lt;double&gt;.Value = 0</c> and
/// <c>Dictionary&lt;double, V&gt;[intExpr]</c> box the widened value as Double.
/// Java widens int→double only while the target stays primitive; at a
/// reference-typed slot <c>setValue(0)</c> / <c>put(0, v)</c> are rejected
/// ("int cannot be converted to java.lang.Double"), so the emitted argument
/// needs an explicit (double) cast.
/// </summary>
public class IntegralToBoxedDoubleTests
{
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

    [Fact]
    public void GenericPropertySetter_IntLiteral_AddsDoubleCast()
    {
        var result = Convert(@"
class ByRef<T>
{
    public T Value { get; set; }
}

class Sample
{
    static double M(ByRef<double> parameter)
    {
        parameter.Value = 0;
        return parameter.Value;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("setValue((double) (0))", result.GeneratedCode);
    }

    [Fact]
    public void DictionaryDoubleKey_IntLiteralAndIntExpression_AddDoubleCast()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    static bool M(Dictionary<double, string> map, double[] values, int key)
    {
        map[0] = ""zero"";
        map[key] = ""key"";
        values[0] = 0;
        string zero = map[0];
        return map.ContainsKey(key);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("put((double) (0)", result.GeneratedCode);
        Assert.Contains("getChecked((double) (0))", result.GeneratedCode);
        Assert.Contains("containsKey((double) (key))", result.GeneratedCode);
        // Primitive double[] slots already accept an int literal in Java.
        Assert.Contains("values[0] = 0;", result.GeneratedCode);
    }

    [Fact]
    public void GenericListAdd_IntLiteral_AddsDoubleCast()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    static void M(List<double> values, int n)
    {
        values.Add(1);
        values.Add(n);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("add((double) (1))", result.GeneratedCode);
        Assert.Contains("add((double) (n))", result.GeneratedCode);
    }

    [Fact]
    public void ChainedGenericPropertyAssignment_IntLiteral_KeepsDoubleTemporary()
    {
        var result = Convert(@"
class ByRef<T>
{
    public T Value { get; set; }
}

class Sample
{
    static void M(ByRef<double> first, ByRef<double> second)
    {
        first.Value = second.Value = 0;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("double __cs2jChain0 = 0;", result.GeneratedCode);
        Assert.Contains("second.setValue(__cs2jChain0)", result.GeneratedCode);
        Assert.Contains("first.setValue(__cs2jChain0)", result.GeneratedCode);
    }
}
