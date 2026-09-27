using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for three related issues with multi-enumeration collection types
/// (types implementing BOTH IEnumerable&lt;T&gt; and IReadOnlyDictionary&lt;K, V&gt;
/// such as the BCL GroupCollection):
///
/// 1. `foreach (T item in groups)` — the Java mapping (CSharpReadOnlyDict) iterates
///    KeyValuePair&lt;K,V&gt;, so the loop must iterate .getValues() (or .getKeys()).
/// 2. `IEnumerator e = groups.GetEnumerator();` — the public non-generic GetEnumerator
///    returns System.Collections.IEnumerator in C#; the Java iterator() yields
///    KeyValuePair and is not assignable to CSharpEnumerator (invariant generics),
///    so it must be wrapped with CSharpEnumerator.from(...).
/// 3. `groups.CopyTo(null, 0)` — with overloads CopyTo(Array, int) / CopyTo(T[], int),
///    C# resolves null to the more specific T[] overload, but Java reports an
///    ambiguous call; the null must be cast to the selected parameter type.
/// </summary>
public class MultiEnumerationCollectionCallSiteTests
{
    private readonly ITestOutputHelper _out;

    public MultiEnumerationCollectionCallSiteTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ForeachOverNonGenericIList_DoesNotUseGetValuesOnAdapter()
    {
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;

class Group { }

class GroupCollection : IList<Group>, IReadOnlyDictionary<string, Group>, IList
{
    public int Count => 0;
    public bool IsReadOnly => true;
    public Group this[int index] { get => null; set { } }
    public IEnumerator GetEnumerator() => throw new System.NotImplementedException();
    IEnumerator<Group> IEnumerable<Group>.GetEnumerator() => throw new System.NotImplementedException();
    public void Add(Group item) { }
    public void Clear() { }
    public bool Contains(Group item) => false;
    public void CopyTo(Group[] array, int arrayIndex) { }
    public bool Remove(Group item) { return false; }
    public int IndexOf(Group item) => -1;
    public void Insert(int index, Group item) { }
    public void RemoveAt(int index) { }
    public Group this[string key] => null;
    public IEnumerable<string> Keys => null;
    public IEnumerable<Group> Values => null;
    public bool ContainsKey(string key) => false;
    public bool TryGetValue(string key, out Group value) { value = null; return false; }

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
    void ICollection.CopyTo(Array array, int index) { }
}

class Test
{
    static void M()
    {
        IList list = new GroupCollection();
        foreach (object item in list)
        {
            System.Console.WriteLine(item);
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The non-generic IList adapter already iterates Object elements; adding
        // .getValues() would call a method the adapter doesn't have.
        Assert.Contains("for (Object item : list)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("list.getValues()", code, StringComparison.Ordinal);
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
