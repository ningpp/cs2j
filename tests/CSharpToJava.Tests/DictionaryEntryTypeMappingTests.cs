using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Collections.DictionaryEntry must map to the compat CSharpDictEntry type
/// (with its import), otherwise generated code like
///   for (Object entry : caps) { DictionaryEntry kvp = (DictionaryEntry)(entry); ... }
/// fails javac with "找不到符号: 类 DictionaryEntry".
/// CSharpHashtable/CSharpIDictionary iterators yield CSharpDictEntry elements, so the
/// enhanced-for source is already correct; only the type reference needs mapping.
/// </summary>
public class DictionaryEntryTypeMappingTests
{
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

    /// <summary>
    /// C# shape from MatchSparse.Dump: foreach (object entry in hashtable) with an
    /// explicit (DictionaryEntry) cast inside the body.
    /// </summary>
    [Fact]
    public void ForeachObjectWithDictionaryEntryCast_MapsToCSharpDictEntry()
    {
        var result = Convert(@"
using System.Collections;
class Sample
{
    void Dump(Hashtable caps)
    {
        if (caps != null)
        {
            foreach (object entry in caps)
            {
                DictionaryEntry kvp = (DictionaryEntry)entry;
                System.Console.WriteLine(kvp.Key + "" -> "" + kvp.Value);
            }
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("CSharpDictEntry kvp = (CSharpDictEntry)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryEntry", result.GeneratedCode.Replace("CSharpDictEntry", string.Empty), StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpDictEntry;", result.GeneratedCode, StringComparison.Ordinal);
        // Hashtable is Iterable in the compat lib: enhanced-for must iterate it directly.
        Assert.Contains("for (Object entry : caps)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Typed loop variable form: foreach (DictionaryEntry kvp in hashtable).
    /// </summary>
    [Fact]
    public void ForeachTypedDictionaryEntryVariable_MapsToCSharpDictEntry()
    {
        var result = Convert(@"
using System.Collections;
class Sample
{
    int SumValues(Hashtable caps)
    {
        int total = 0;
        foreach (DictionaryEntry kvp in caps)
        {
            total += kvp.Value.ToString().Length;
        }
        return total;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("CSharpDictEntry", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryEntry", result.GeneratedCode.Replace("CSharpDictEntry", string.Empty), StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpDictEntry;", result.GeneratedCode, StringComparison.Ordinal);
    }
}
