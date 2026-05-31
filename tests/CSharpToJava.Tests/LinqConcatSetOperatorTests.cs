using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LinqConcatSetOperatorTests
{
    [Fact]
    public void Concat_SelectChain_BothSequencesIterated()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<string> ConcatLists(IList<string> strs1, IList<string> strs2) {
        return strs1.Select(str => str).Concat(strs2.Select(str => str));
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;

        Assert.Contains("Iterable<String> concatLists(List<String> strs1, List<String> strs2)", code, StringComparison.Ordinal);

        Assert.Contains("ProceduralLinq", code, StringComparison.Ordinal);

        Assert.DoesNotContain("yield", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Concat(", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(", code, StringComparison.Ordinal);

        Assert.Contains("Iterable<String> _second", code, StringComparison.Ordinal);

        Assert.Matches(@"for\s*\(\s*String\s+\w+\s*:\s*_linqitems\s*\)", code);
        Assert.Matches(@"for\s*\(\s*String\s+\w+\s*:\s*_second\s*\)", code);

        Assert.Contains("_list.add(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Concat_Simple_BothSequencesIterated()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<string> M(List<string> a, List<string> b) {
        return a.Concat(b);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        var bothUsed = code.Contains("Stream.concat", StringComparison.Ordinal)
                    || code.Contains("_second", StringComparison.Ordinal);
        Assert.True(bothUsed, "Expected Stream.concat or _second for Concat second sequence");
    }

    [Fact]
    public void Union_BothSequencesIterated()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<int> M(List<int> a, List<int> b) {
        return a.Union(b);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        var bothUsed = code.Contains("_second", StringComparison.Ordinal)
                    || code.Contains("_unionSeen", StringComparison.Ordinal)
                    || code.Contains("Stream.concat", StringComparison.Ordinal);
        Assert.True(bothUsed, "Expected second sequence handling for Union");
    }

    [Fact]
    public void Intersect_UsesSecondSequence()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<int> M(List<int> a, List<int> b) {
        return a.Intersect(b);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        var secondUsed = code.Contains("_secondSet", StringComparison.Ordinal)
                      || code.Contains("HashSet", StringComparison.Ordinal);
        Assert.True(secondUsed, "Expected HashSet or _secondSet for Intersect");
    }

    [Fact]
    public void Except_UsesSecondSequence()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<int> M(List<int> a, List<int> b) {
        return a.Except(b);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        var secondUsed = code.Contains("_secondSet", StringComparison.Ordinal)
                      || code.Contains("HashSet", StringComparison.Ordinal);
        Assert.True(secondUsed, "Expected HashSet or _secondSet for Except");
    }

    [Fact]
    public void Concat_WithSelectBefore_BothSequencesInResult()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<string> M(List<string> a, List<string> b) {
        return a.Select(x => x.ToUpper()).Concat(b.Select(x => x.ToLower()));
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("_second", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }
}
