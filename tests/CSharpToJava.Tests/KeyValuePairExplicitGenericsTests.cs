using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new KeyValuePair with explicit generic types preserves them
/// in the Java AbstractMap.SimpleEntry construction and casts to Map.Entry
/// for generic invariance compatibility.
/// </summary>
public class KeyValuePairExplicitGenericsTests
{
    [Fact]
    public void KeyValuePair_WithExplicitGenerics_CastsToMapEntry()
    {
        var result = Convert(@"
using System.Collections.Generic;

interface IRectangle<T> { }
class Rectangle : IRectangle<int> { }

class Test
{
    Map.Entry<IRectangle<int>, string> Create(Rectangle r, string s)
    {
        return new KeyValuePair<IRectangle<int>, string>(r, s);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // Should have explicit generic args, not diamond inference
        Assert.Contains("new AbstractMap.SimpleEntry<IRectangle<Integer>, String>", code);
        // Should cast to Map.Entry for interface compatibility
        Assert.Contains("(Map.Entry<IRectangle<Integer>, String>)", code);
    }

    [Fact]
    public void KeyValuePair_WithoutExplicitGenerics_UsesDiamond()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test
{
    void Use()
    {
        var pair = new KeyValuePair<string, int>(""hello"", 42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // Should have explicit type args AND cast since they were explicit in C#
        Assert.Contains("new AbstractMap.SimpleEntry<String, Integer>", code);
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
