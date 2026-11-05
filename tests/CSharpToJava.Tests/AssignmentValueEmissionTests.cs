using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# 赋值表达式在值位置（表达式体方法 / 值 lambda）有值，而 Java 赋值没有值，
/// 展开后的属性 setter 还是 void。回归真实链 csharp-sdk regular-expressions 测试：
/// closure-lift 生成的 `object Lambda() => Regex.CacheSize = -1;` 以及
/// `() => r.Caps = null` 传给 Func&lt;object&gt;。
/// </summary>
public class AssignmentValueEmissionTests
{
    [Fact]
    public void ExpressionBodiedAssignment_ReturnsAssignedValue()
    {
        var result = Convert(@"
class Regex
{
    public static int CacheSize { get; set; }
}

class Holder
{
    public static object Lambda() => Regex.CacheSize = -1;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("Regex.setCacheSize(-1);", code);
        Assert.Contains("return Regex.getCacheSize();", code);
        Assert.DoesNotContain("return Regex.setCacheSize(-1)", code);
    }

    [Fact]
    public void ValueLambdaAssignment_ReturnsAssignedValue()
    {
        var result = Convert(@"
using System;
using System.Collections;

class Config
{
    public IDictionary Caps { get; set; }
}

class Holder
{
    public static void Run(object value, Func<object> f) { }

    public static void M(Config r)
    {
        Run(null, () => r.Caps = null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("r.setCaps(null);", code);
        Assert.Contains("return r.getCaps();", code);
        Assert.DoesNotContain("return r.setCaps(null)", code);
    }

    /// <summary>
    /// C# Dictionary&lt;K,V&gt; 实现非泛型 IDictionary，compat 的 CSharpDictionary 与
    /// CSharpIDictionary 却无继承关系：属性槽位需要 fromAny 适配视图。
    /// </summary>
    [Fact]
    public void DictionaryValue_ToNonGenericIDictionaryProperty_UsesAdapter()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class Config
{
    public IDictionary Caps { get; set; }
}

class Holder
{
    public static void M(Config r)
    {
        r.Caps = new Dictionary<string, string>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("CSharpIDictionary.fromAny(new CSharpDictionary<String, String>())", code);
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
