using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a class implementing both IList&lt;T&gt; and non-generic IList with explicit
/// implementations of Contains/IndexOf/Remove does not emit duplicate erased methods.
/// Reproduces CaptureCollection: javac "method contains(Object) is already defined".
/// </summary>
public class GenericAndNonGenericIListCollisionTests
{
    private const string Source = @"
using System.Collections;
using System.Collections.Generic;

public class Item { }

public class ItemCollection : IList<Item>, IReadOnlyList<Item>, IList
{
    public int Count => 0;
    public bool IsReadOnly => true;
    public Item this[int index] => null;

    public IEnumerator GetEnumerator() => null;
    IEnumerator<Item> IEnumerable<Item>.GetEnumerator() => null;

    int IList<Item>.IndexOf(Item item) => -1;
    void IList<Item>.Insert(int index, Item item) => throw new System.NotSupportedException();
    void IList<Item>.RemoveAt(int index) => throw new System.NotSupportedException();
    Item IList<Item>.this[int index] { get => null; set => throw new System.NotSupportedException(); }

    void ICollection<Item>.Add(Item item) => throw new System.NotSupportedException();
    void ICollection<Item>.Clear() => throw new System.NotSupportedException();
    bool ICollection<Item>.Contains(Item item) => ((IList<Item>)this).IndexOf(item) >= 0;
    void ICollection<Item>.CopyTo(Item[] array, int arrayIndex) { }
    bool ICollection<Item>.Remove(Item item) => throw new System.NotSupportedException();

    int IList.Add(object value) => throw new System.NotSupportedException();
    bool IList.Contains(object value) => value is Item other && ((ICollection<Item>)this).Contains(other);
    int IList.IndexOf(object value) => value is Item other ? ((IList<Item>)this).IndexOf(other) : -1;
    void IList.Insert(int index, object value) => throw new System.NotSupportedException();
    void IList.Remove(object value) => throw new System.NotSupportedException();
    void IList.RemoveAt(int index) => throw new System.NotSupportedException();
    bool IList.IsFixedSize => true;
    object IList.this[int index] { get => null; set => throw new System.NotSupportedException(); }

    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    void ICollection.CopyTo(System.Array array, int index) { }
}";

    [Fact]
    public void DualIListImplementation_NoDuplicateErasedMethods()
    {
        var result = Convert(Source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        AssertSingle(code, "contains(Object");
        AssertSingle(code, "indexOf(Object");
        AssertSingle(code, "remove(Object");
    }

    private static void AssertSingle(string code, string needle)
    {
        var count = CountOccurrences(code, needle);
        Assert.True(count == 1, $"'{needle}' appears {count} times\n---Generated---\n{code}");
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
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
