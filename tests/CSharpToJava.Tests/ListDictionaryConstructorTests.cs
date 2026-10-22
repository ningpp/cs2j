using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# <c>new List&lt;KeyValuePair&lt;K,V&gt;&gt;(dictionary)</c> binds the
/// <c>List&lt;T&gt;(IEnumerable&lt;T&gt;)</c> overload because Dictionary&lt;K,V&gt;
/// implements IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;. The conversion emits the same
/// <c>new CSharpList&lt;...&gt;(dict)</c> shape; the compat constructor
/// <c>CSharpList(CSharpIterable)</c> is what makes it compile on the Java side, since
/// compat maps are Iterable but deliberately not Collections.
/// </summary>
public class ListDictionaryConstructorTests
{
    [Fact]
    public void ListConstructedFromDictionary_KeepsCollectionConstructorShape()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    public List<KeyValuePair<string, string>> Copy(Dictionary<string, string> dict)
    {
        return new List<KeyValuePair<string, string>>(dict);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("new CSharpList<CSharpKeyValuePair<String, String>>(dict)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void VarLocalListFromDictionary_KeepsCollectionConstructorShape()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    public int Count(Dictionary<string, string> dict)
    {
        var temp = new List<KeyValuePair<string, string>>(dict);
        return temp.Count;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("var temp = new CSharpList<CSharpKeyValuePair<String, String>>(dict);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ListConstructedFromList_StillUsesCollectionConstructor()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    public List<int> Copy(List<int> source)
    {
        return new List<int>(source);
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("new CSharpList<Integer>(source)", result.GeneratedCode, StringComparison.Ordinal);
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
