using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Dictionary{K,V}.KeyCollection maps to Set{K}
/// and Dictionary{K,V}.ValueCollection maps to Collection{V} in Java.
/// </summary>
public class DictionaryKeyCollectionMappingTests
{
    [Fact]
    public void DictionaryKeysProperty_MapsToSet()
    {
        var source = @"
using System.Collections.Generic;

class Sample {
    void M(Dictionary<string, int> dict) {
        Dictionary<string, int>.KeyCollection keys = dict.Keys;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Set<String>", result.GeneratedCode);
        Assert.DoesNotContain("KeyCollection", result.GeneratedCode);
    }

    [Fact]
    public void DictionaryValueCollection_MapsToCollection()
    {
        var source = @"
using System.Collections.Generic;

class Sample {
    void M(Dictionary<string, int> dict) {
        Dictionary<string, int>.ValueCollection vals = dict.Values;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Collection<Integer>", result.GeneratedCode);
        Assert.DoesNotContain("ValueCollection", result.GeneratedCode);
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
