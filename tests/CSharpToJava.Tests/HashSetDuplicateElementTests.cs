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
        Assert.Contains("Set.of(", code, StringComparison.Ordinal);
        // The duplicate ',' should be removed - count occurrences of "','"
        var count = CountOccurrences(code, "','");
        Assert.Equal(1, count);
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
        Assert.Contains("Set.of(", code, StringComparison.Ordinal);
        // All elements unique - should have 4 char literals in Set.of()
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
        Assert.Contains("Set.of(", code, StringComparison.Ordinal);
        // All elements are duplicates - only one "1" should remain
        var count = CountOccurrences(code, "1,");
        Assert.Equal(0, count);
        // Should have exactly one "1" in Set.of()
        var setOfContent = ExtractSetOfContent(code);
        Assert.Equal("1", setOfContent.Trim());
    }

    private static int CountOccurrences(string source, string substring)
    {
        int count = 0;
        int pos = 0;
        while ((pos = source.IndexOf(substring, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += substring.Length;
        }
        return count;
    }

    private static string ExtractSetOfContent(string code)
    {
        var startIdx = code.IndexOf("Set.of(", StringComparison.Ordinal);
        if (startIdx < 0) return string.Empty;
        startIdx += "Set.of(".Length;
        var endIdx = code.IndexOf(')', startIdx);
        if (endIdx < 0) return string.Empty;
        return code.Substring(startIdx, endIdx - startIdx);
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
