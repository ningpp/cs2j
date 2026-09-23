using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LinqConcatSetOperatorTests
{

    [Fact]
    public void Concat_ReturnFromIEnumerable_DoesNotDuplicateCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<string> M(IEnumerable<string> a, IEnumerable<string> b) {
        return a.Concat(b);
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode;
        // The return wrapper must not add a second .collect() when Concat already produces one
        var collectCount = System.Text.RegularExpressions.Regex.Matches(code, @"\.collect\(CSharpList\.toCSharpList\(\)\)").Count;
        Assert.True(collectCount <= 1, $"Expected at most 1 .collect(CSharpList.toCSharpList()) but found {collectCount} in:\n{code}");
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
