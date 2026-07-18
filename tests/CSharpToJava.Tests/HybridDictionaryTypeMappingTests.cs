using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class HybridDictionaryTypeMappingTests
{
    [Fact]
    public void HybridDictionary_Field_MappedToCSharpHashtable()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System.Collections.Specialized;

class Sample
{
    private HybridDictionary _documentURIs = new HybridDictionary();

    public void Add(string href)
    {
        _documentURIs.Add(href, null);
        _documentURIs.Remove(href);
        bool b = _documentURIs.Contains(href);
    }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("HybridDictionary", code, StringComparison.Ordinal);
        Assert.Contains("CSharpHashtable", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpHashtable;", code, StringComparison.Ordinal);
    }
}
