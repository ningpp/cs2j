using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that custom dictionary-like types (with "Dictionary" in the name)
/// have their indexer setter named "put" (not "set"), consistent with
/// AssignmentTransformer's IsDictionaryLikeContainer logic.
/// </summary>
public class DictionaryLikeIndexerSetterTests
{
    [Fact]
    public void LowLevelDictionary_IndexerSetter_NamedPut()
    {
        var source = @"
using System;
using System.Collections.Generic;

namespace System.Collections.Generic
{
    internal sealed class LowLevelDictionary<TKey, TValue>
    {
        private int[] _buckets;
        private Entry[] _entries;

        public TValue this[TKey key]
        {
            get { return default; }
            set { }
        }

        public void Add(TKey key, TValue value) { }

        private class Entry
        {
            public TKey _key;
            public TValue _value;
        }
    }
}

class Usage
{
    void M()
    {
        var dict = new LowLevelDictionary<string, int>();
        dict[""key""] = 42;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // The indexer setter should be named "put" (not "set") for dictionary-like types
        Assert.Contains("put(", result.GeneratedCode);
        Assert.DoesNotContain(".set(", result.GeneratedCode);
        // The assignment dict["key"] = 42 should use put()
        Assert.Contains("dict.put(", result.GeneratedCode);
    }

    [Fact]
    public void NonDictionaryType_IndexerSetter_NamedSet()
    {
        var source = @"
class MyContainer
{
    private int[] _items;
    public int this[int index]
    {
        get { return _items[index]; }
        set { _items[index] = value; }
    }
}

class Usage
{
    void M()
    {
        var c = new MyContainer();
        c[0] = 42;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Non-dictionary types should use "set" for the indexer setter
        Assert.Contains("public int set(int index, int value)", result.GeneratedCode);
        Assert.DoesNotContain("public int put(int index, int value)", result.GeneratedCode);
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
