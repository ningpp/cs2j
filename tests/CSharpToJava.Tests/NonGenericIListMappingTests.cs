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

    /// <summary>
    /// C# arrays covary into the IList family (T[] implements IList&lt;T&gt;);
    /// Java has no such relation, so assignments into IList-family slots must
    /// wrap the array in the CSharpIList.from(ArrayHelper.asListView(arr)) view
    /// (real chain: FunctionQuery/StringFunctions copy constructors
    /// `_args = (Query[])...`).
    /// </summary>
    [Fact]
    public void ArrayAssignment_ToIListField_WrapsArrayView()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Item { }

class Holder
{
    private IList<Item> _items;
    private IList<Item> _listBacked;

    public Holder(IList<Item> items)
    {
        var tmp = new Item[] { new Item() };
        _items = tmp;
        _listBacked = new List<Item>();
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpIList.from(ArrayHelper.asListView(tmp))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpIList.from(new CSharpList<Item>", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dictionary&lt;K,V&gt;.Values (static type: nested ValueCollection implementing
    /// ICollection&lt;V&gt;) renders as the CSharpICollection view of the compat
    /// dictionary; returning it through an ICollection slot needs the
    /// CSharpIList.from adapter (real chain: serialization NameTable.Values).
    /// </summary>
    [Fact]
    public void DictionaryValuesReturn_ToICollection_WrapsAdapterView()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class Table
{
    private Dictionary<int, object> _table = new Dictionary<int, object>();

    internal ICollection Values
    {
        get { return _table.Values; }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpIList.from(_table.getValues())", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// An array argument to an IList&lt;T&gt;/IList parameter maps to the CSharpIList
    /// family, so the call site must pass the CSharpIList.from(asListView(...))
    /// view - the CSharpGenericIList.fromTyped spelling never converts to
    /// CSharpIList (real chain: SortQuery.AddFunction passing
    /// new Query[] to StringFunctions(IList&lt;Query&gt;)).
    /// </summary>
    [Fact]
    public void ArrayArgument_ToIListParameter_UsesCSharpIListView()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Query { }

class Sink
{
    public Sink(IList<Query> argList) { _argList = argList; }
    private IList<Query> _argList;
}

class Source
{
    public object Build(Query q)
    {
        return new Sink(new Query[] { q });
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("CSharpGenericIList.fromTyped(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpIList.from(ArrayHelper.asListView(new Query[]{ q }))", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// A class implementing the non-generic IList AND a generic IList&lt;T&gt; maps to a
    /// Java class that already implements CSharpIList (真实链 CaptureCollection), so the
    /// assignment must not wrap it in the read-only bridge — its default get/contains/
    /// indexOf throw UnsupportedOperationException.
    /// </summary>
    [Fact]
    public void ClassImplementingGenericAndNonGenericIList_AssignsWithoutAdapter()
    {
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;

class Capture { }
class Captures : IList<Capture>, IReadOnlyList<Capture>, IList
{
    public Capture this[int index] => null;
    object IList.this[int index] { get => null; set { } }
    public int Count => 0;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void Add(Capture item) { }
    int IList.Add(object value) => 0;
    public void Clear() { }
    public bool Contains(Capture item) => false;
    bool IList.Contains(object value) => false;
    public void CopyTo(Capture[] array, int arrayIndex) { }
    void ICollection.CopyTo(System.Array array, int index) { }
    public IEnumerator<Capture> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
    public int IndexOf(Capture item) => -1;
    int IList.IndexOf(object value) => -1;
    public void Insert(int index, Capture item) { }
    void IList.Insert(int index, object value) { }
    public bool Remove(Capture item) => false;
    void IList.Remove(object value) { }
    public void RemoveAt(int index) { }
}

class Host
{
    static Captures Create() => new Captures();
    static void Use()
    {
        IList<Capture> generic = Create();
        IList nonGeneric = Create();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("CSharpIList.from(Create())", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpIList<Capture> generic = create();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpIList nonGeneric = create();", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// A class implementing only the non-generic ICollection (真实链
    /// XmlAttributeCollection : XmlNamedNodeMap, ICollection) maps to a Java class
    /// implementing CSharpCollection only, so an IList slot needs the adapter view.
    /// </summary>
    [Fact]
    public void CollectionOnlyClass_IntoIListSlot_GetsAdapterView()
    {
        var result = Convert("""
using System.Collections;

class Attributes : ICollection
{
    public int Count => 0;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void CopyTo(System.Array array, int index) { }
    public IEnumerator GetEnumerator() => null;
}

class Host
{
    static Attributes Get() => new Attributes();
    static void Use()
    {
        IList list = Get();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpIList list = CSharpIList.from(get());", result.GeneratedCode, StringComparison.Ordinal);
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
