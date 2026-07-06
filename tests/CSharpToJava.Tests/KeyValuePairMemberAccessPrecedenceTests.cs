using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new KeyValuePair&lt;K,V&gt;(...).Key generates correct member access
/// using CSharpKeyValuePair's getValue() method.
/// </summary>
public class KeyValuePairMemberAccessPrecedenceTests
{
    [Fact]
    public void KeyValuePair_MemberAccess_HasCorrectCastPrecedence()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test
{
    int GetKey(string a, int b)
    {
        return new KeyValuePair<string, int>(a, b).Value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // CSharpKeyValuePair has getValue() directly, no cast needed
        Assert.Contains("new CSharpKeyValuePair(a, b).getValue()", code);
    }

    [Fact]
    public void KeyValuePair_InFilter_HasCorrectCastPrecedence()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Test
{
    List<KeyValuePair<int, int>> Filter(List<KeyValuePair<string, string>> items)
    {
        return items
            .Select(item => new KeyValuePair<int, int>(item.Key.Length, item.Value.Length))
            .Where(kv => kv.Key != -1 && kv.Value != -1)
            .ToList();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // CSharpKeyValuePair is used directly with explicit type args
        Assert.Contains("new CSharpKeyValuePair<Integer, Integer>", code);
        Assert.Contains(".getKey()", code);
        Assert.Contains(".getValue()", code);
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
