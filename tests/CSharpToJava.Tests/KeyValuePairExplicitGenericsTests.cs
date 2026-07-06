using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new KeyValuePair with explicit generic types preserves them
/// in the Java CSharpKeyValuePair construction.
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
        // Should have explicit generic args preserved
        Assert.Contains("new CSharpKeyValuePair<IRectangle<Integer>, String>", code);
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
        // Should have explicit type args
        Assert.Contains("new CSharpKeyValuePair<String, Integer>", code);
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
