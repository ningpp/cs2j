using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CustomTryGetValueGuardTests
{

    [Fact]
    public void DictionaryInterfaceClass_TryGetValue_StillUsesGeneratedTryGetValue()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class MyDict : IReadOnlyDictionary<string, int>
{
    public bool TryGetValue(string key, out int value) { value = 0; return false; }
    public int Count { get; }
    public int this[string key] => 0;
    public IEnumerable<string> Keys => null;
    public IEnumerable<int> Values => null;
    public bool ContainsKey(string key) => false;
    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => null;
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
}

public class Sample
{
    public static int M(MyDict d, string key)
    {
        if (d.TryGetValue(key, out int v))
        {
            return v;
        }
        return -1;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("tryGetValue(", code, StringComparison.Ordinal);
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
