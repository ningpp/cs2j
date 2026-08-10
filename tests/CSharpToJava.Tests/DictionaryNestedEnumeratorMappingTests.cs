using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nested-type references (Dictionary&lt;K,V&gt;.Enumerator) resolve
/// the outer type through the type mapping table — the compat nested class is
/// CSharpDictionary.Enumerator, not an unqualified "Dictionary" which javac
/// cannot find. Reproduces DictionaryOfTKeyTValueConverter.OnWriteResume.
/// </summary>
public class DictionaryNestedEnumeratorMappingTests
{
    [Fact]
    public void DictionaryEnumerator_ResolvesThroughTypeMapping()
    {
        var result = Convert(@"
class Holder
{
    public void M(System.Collections.Generic.Dictionary<string, int> dict)
    {
        System.Collections.Generic.Dictionary<string, int>.Enumerator e = (System.Collections.Generic.Dictionary<string, int>.Enumerator)dict.GetEnumerator();
        e.MoveNext();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CSharpDictionary.Enumerator", code);
        Assert.DoesNotContain("(Dictionary.Enumerator)", code);
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
