using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that GetEnumerator() on a dictionary-like receiver is adapted
/// to the target enumerator type (IDictionaryEnumerator vs IEnumerator&lt;T&gt;).
/// </summary>
public class DictionaryEnumeratorMappingTests
{
    [Fact]
    public void DictionaryGetEnumerator_AssignedToIDictionaryEnumerator_WrapsWithDictEnumerator()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class SchemaHelper
{
    public void IterateAttributes(Dictionary<string, int> attDefs)
    {
        IDictionaryEnumerator e = attDefs.GetEnumerator();
        while (e.MoveNext())
        {
            int value = (int)e.Value;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpDictEnumerator", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpDictEnumerator.from", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".iterator())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DictionaryGetEnumerator_AssignedToGenericEnumerator_DoesNotWrap()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class SchemaHelper
{
    public void IterateAttributes(Dictionary<string, int> attDefs)
    {
        IEnumerator<KeyValuePair<string, int>> e = attDefs.GetEnumerator();
        while (e.MoveNext())
        {
            int value = e.Current.Value;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpGenericEnumerator", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpDictEnumerator.from", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void HashtableGetEnumerator_AssignedToIDictionaryEnumerator_WrapsWithDictEnumerator()
    {
        var result = Convert(@"
using System.Collections;

public class SchemaHelper
{
    public void IterateAttributes(Hashtable attDefs)
    {
        IDictionaryEnumerator e = attDefs.GetEnumerator();
        while (e.MoveNext())
        {
            object value = e.Value;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpDictEnumerator", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpDictEnumerator.from", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomEnumeratorGetEnumerator_AssignedToCustomEnumerator_CastsIterator()
    {
        var result = Convert(@"
using System.Collections;

public class MyCollection : ICollection
{
    public MyEnumerator GetEnumerator() => new MyEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int Count => 0;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void CopyTo(Array array, int index) { }
}

public sealed class MyEnumerator : IEnumerator
{
    public bool MoveNext() => false;
    public object Current => null;
    public void Reset() { }
}

public class TestDriver
{
    public void Iterate(MyCollection col)
    {
        for (MyEnumerator e = col.GetEnumerator(); e.MoveNext(); )
        {
            object value = e.Current;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        System.Console.WriteLine(result.GeneratedCode);
        Assert.Contains("MyEnumerator", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(MyEnumerator)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpEnumerator.from", result.GeneratedCode, StringComparison.Ordinal);
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
