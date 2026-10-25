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
