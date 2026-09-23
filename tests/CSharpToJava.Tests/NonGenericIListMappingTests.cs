using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that non-generic System.Collections.IList is mapped to a Java type
/// that allows adding concrete elements (CSharpGenericIList&lt;Object&gt;).
/// </summary>
public class NonGenericIListMappingTests
{

    [Fact]
    public void NonGenericIList_LocalDeclaration_WrapsConcreteGenericList()
    {
        var result = Convert(@"using System.Collections;
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
    void IList<Item>.Insert(int index, Item item) { }
    void IList<Item>.RemoveAt(int index) { }
    Item IList<Item>.this[int index] { get => null; set { } }
    void ICollection<Item>.Add(Item item) { }
    void ICollection<Item>.Clear() { }
    bool ICollection<Item>.Contains(Item item) => false;
    void ICollection<Item>.CopyTo(Item[] array, int arrayIndex) { }
    bool ICollection<Item>.Remove(Item item) => false;

    int IList.Add(object value) => 0;
    bool IList.Contains(object value) => false;
    int IList.IndexOf(object value) => -1;
    void IList.Insert(int index, object value) { }
    void IList.Remove(object value) { }
    void IList.RemoveAt(int index) { }
    bool IList.IsFixedSize => true;
    object IList.this[int index] { get => null; set { } }

    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    void ICollection.CopyTo(System.Array array, int index) { }
}

public class C
{
    void M()
    {
        IList list = new ItemCollection();
        list.Add((object)null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Non-generic IList must keep the CSharpGenericIList<Object> contract and
        // wrap the concrete generic list so add(Object)/insert(int,Object) resolve.
        Assert.Contains("CSharpGenericIList.from(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var list = new ItemCollection()", code, StringComparison.Ordinal);
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
