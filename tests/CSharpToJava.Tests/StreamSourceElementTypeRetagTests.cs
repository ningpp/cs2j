using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// LINQ stream sources over a collection whose Java carrier erases the element
/// type (a generated ICollection&lt;T&gt;/IList&lt;T&gt; class implements
/// CSharpIList&lt;T&gt; extends the non-generic CSharpCollection extends
/// CSharpIterable&lt;Object&gt;) must be re-typed to the C# element type before
/// <c>.spliterator()</c>. Otherwise the stream is <c>Stream&lt;Object&gt;</c> and a
/// lifted lambda taking the element type cannot bind to <c>Predicate&lt;Object&gt;</c>
/// ("方法引用无效: Object无法转换为T").
/// </summary>
public class StreamSourceElementTypeRetagTests
{
    [Fact]
    public void Where_OnGenericCollectionParameter_RetagsIterableToElementType()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Bag<T>
{
    public bool M(ICollection<T> xs)
    {
        return xs.Where(x => x != null).Any(x => x != null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(
            "StreamSupport.stream(((Iterable<T>)(Iterable) (xs)).spliterator(), false)",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StreamSupport.stream((xs).spliterator()",
            result.GeneratedCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Any_OnCustomCollectionClass_RetagsIterableToElementType()
    {
        var result = Convert(@"
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

class MyColl<T> : ICollection<T>
{
    private readonly List<T> items = new List<T>();
    public int Count { get { return items.Count; } }
    public bool IsReadOnly { get { return false; } }
    public void Add(T item) { items.Add(item); }
    public void Clear() { items.Clear(); }
    public bool Contains(T item) { return items.Contains(item); }
    public void CopyTo(T[] array, int arrayIndex) { items.CopyTo(array, arrayIndex); }
    public bool Remove(T item) { return items.Remove(item); }
    public IEnumerator<T> GetEnumerator() { return items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() { return items.GetEnumerator(); }
}

class Holder<T>
{
    public bool M(MyColl<T> xs)
    {
        return xs.Any(p => p != null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(
            "StreamSupport.stream(((Iterable<T>)(Iterable) (xs)).spliterator(), false)",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StreamSupport.stream((xs).spliterator()",
            result.GeneratedCode,
            StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            // Keep the LINQ channel (Java Stream) emission: this is the path that
            // opens the stream over the erased collection and must re-type it.
            Options = new ConversionOptions { EnableLinqRewrite = false },
        });
    }
}
