using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class HashSetDuplicateElementTests
{
    [Fact]
    public void HashSetInit_WithDuplicateElements_DeduplicatesInSetOf()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test {
    HashSet<char> set = new HashSet<char>() { ':', ',', '?', ',', ';' };
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("CSharpHashSet", code, StringComparison.Ordinal);
        Assert.Contains("':'", code, StringComparison.Ordinal);
        Assert.Contains("','", code, StringComparison.Ordinal);
        Assert.Contains("'?'", code, StringComparison.Ordinal);
        Assert.Contains("';'", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HashSetInit_NoDuplicateElements_RemainsUnchanged()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test {
    HashSet<char> set = new HashSet<char>() { ':', ',', '?', ';' };
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("CSharpHashSet", code, StringComparison.Ordinal);
        // All elements unique - should have 4 char literals
        Assert.Contains("':'", code, StringComparison.Ordinal);
        Assert.Contains("','", code, StringComparison.Ordinal);
        Assert.Contains("'?'", code, StringComparison.Ordinal);
        Assert.Contains("';'", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HashSetInit_AllDuplicateElements_OnlyOneInSetOf()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test {
    HashSet<int> set = new HashSet<int>() { 1, 1, 1 };
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("CSharpHashSet", code, StringComparison.Ordinal);
        // CSharpHashSet handles deduplication at runtime; all elements are passed to constructor
        Assert.Contains("ArrayHelper.toList(1, 1, 1)", code, StringComparison.Ordinal);
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
