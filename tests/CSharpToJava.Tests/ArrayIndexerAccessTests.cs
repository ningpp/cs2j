using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ArrayIndexerAccessTests
{
    [Fact]
    public void ArrayElementAccess_UsesBracketNotation()
    {
        var result = Convert(@"
class Test {
    void M(int[] arr) {
        int x = arr[0];
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("arr[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IListIndexer_OnArrayVariable_UsesBracketNotation()
    {
        // When a variable is typed as IList<T> but holds an array, the indexer
        // access should still use bracket notation in Java.
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(IList<int> list) {
        int x = list[0];
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("list.get(0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationOnArrayElement_WithConditionalIndex_PreservesIndexExpression()
    {
        var result = Convert("""
using System;

class Item
{
    public void Add(int value) {}
}

class Test
{
    void M(Item[] items, Random random, int count)
    {
        items[random.Next(count)].Add(1);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("items[random.next(count)].add(1);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("items[((count) .add", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void VarLocal_FromRegexSplit_UsesArrayBracketAccess()
    {
        var result = Convert("""
using System;
using System.Text.RegularExpressions;

class Test
{
    void M(string line)
    {
        var parts = Regex.Split(line, @"\s{2,}");
        int value = Int32.Parse(parts[0]);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.True(
            result.GeneratedCode.Contains("parts[0]", StringComparison.Ordinal),
            result.GeneratedCode);
        Assert.DoesNotContain("parts.get(0)", result.GeneratedCode, StringComparison.Ordinal);
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
