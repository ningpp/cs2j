using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nested type references within the same enclosing class
/// use simple names (not qualified "OuterClass.InnerClass") to avoid
/// Java raw type issues with generics.
/// </summary>
public class NestedTypeSimpleNameTests
{
    [Fact]
    public void NestedTypeInSameClass_UsesSimpleName()
    {
        var source = @"
using System;

class Outer<TKey, TValue>
{
    private Entry[] _entries;

    private class Entry
    {
        public TKey Key;
        public TValue Value;
        public Entry Next;
    }

    public TValue Get(TKey key)
    {
        Entry e = Find(key);
        return e.Value;
    }

    private Entry Find(TKey key)
    {
        return _entries[0];
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Should use "Entry" not "Outer.Entry" to preserve generic type parameters
        Assert.Contains("Entry<TKey, TValue> e =", result.GeneratedCode);
        Assert.DoesNotContain("Outer.Entry", result.GeneratedCode);
    }

    [Fact]
    public void NestedTypeInDifferentClass_UsesQualifiedName()
    {
        var source = @"
class Container
{
    public class Item
    {
        public int Value;
    }
}

class User
{
    Container.Item CreateItem()
    {
        return new Container.Item();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Should use qualified name when referencing from a different class
        Assert.Contains("Container.Item", result.GeneratedCode);
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
