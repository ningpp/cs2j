using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new List(dictionary.Keys) produces new ArrayList(dictionary.keySet())
/// without an unnecessary (Iterable) cast that would break the ArrayList(Collection) constructor.
/// </summary>
public class ListFromDictionaryKeysConstructorTests
{
    [Fact]
    public void NewList_FromDictionaryKeys_NoIterableCast()
    {
        var source = @"
using System.Collections.Generic;

class Sample {
    void M() {
        var dict = new Dictionary<string, int>();
        var keys = new List<string>(dict.Keys);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Should produce new ArrayList<String>(dict.keySet()) not (Iterable<String>)(Iterable<?>)(dict.keySet())
        Assert.Contains("new ArrayList<String>(dict.keySet())", result.GeneratedCode);
        Assert.DoesNotContain("Iterable<?>", result.GeneratedCode);
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
