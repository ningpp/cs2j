using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConcurrentDictionaryGetOrAddTests
{
    [Fact]
    public void GetOrAdd_WithValue_MapsToConcurrentHashMapHelper()
    {
        var result = Convert(@"
using System.Collections.Concurrent;

public class Sample
{
    private readonly ConcurrentDictionary<string, int> _cache = new ConcurrentDictionary<string, int>();

    public int Get(string key)
    {
        return _cache.GetOrAdd(key, 42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ConcurrentHashMapHelper.getOrAdd(_cache, key, 42)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ConcurrentHashMapHelper", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_cache.getOrAdd(", code, StringComparison.Ordinal);
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
