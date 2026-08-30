using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that nested collection classes implementing ICollection<Object> have their
/// iterator() return type upgraded to CSharpGenericEnumerator<Object> and their
/// nested enumerator class also upgraded to implement CSharpGenericEnumerator<Object>.
/// Reproduces the XmlSchemaObjectTable pattern where the outer class does NOT implement
/// IEnumerable itself, but its nested NamesCollection/ValuesCollection classes do.
/// </summary>
public class NestedCollectionEnumeratorUpgradeTests
{
    [Fact]
    public void NestedCollectionICollectable_EnumeratorUpgradedToGeneric()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class MyObjectTable
{
    private List<Entry> _entries = new List<Entry>();

    public ICollection Names
    {
        get { return new NamesCollection(_entries, _entries.Count); }
    }

    public ICollection Values
    {
        get { return new ValuesCollection(_entries, _entries.Count); }
    }

    internal struct Entry
    {
        internal string qname;
        internal object xso;
    }

    internal class NamesCollection : ICollection
    {
        private List<Entry> _entries;
        private int _size;

        internal NamesCollection(List<Entry> entries, int size)
        {
            _entries = entries;
            _size = size;
        }

        public int Count => _size;
        public bool IsSynchronized => false;
        public object SyncRoot => null;
        public void CopyTo(Array array, int index) { }

        public IEnumerator GetEnumerator()
        {
            return new MyEnumerator(_entries, _size);
        }
    }

    internal class ValuesCollection : ICollection
    {
        private List<Entry> _entries;
        private int _size;

        internal ValuesCollection(List<Entry> entries, int size)
        {
            _entries = entries;
            _size = size;
        }

        public int Count => _size;
        public bool IsSynchronized => false;
        public object SyncRoot => null;
        public void CopyTo(Array array, int index) { }

        public IEnumerator GetEnumerator()
        {
            return new MyEnumerator(_entries, _size);
        }
    }

    internal class MyEnumerator : IEnumerator
    {
        private List<Entry> _entries;
        private int _index = -1;
        private int _size;

        internal MyEnumerator(List<Entry> entries, int size)
        {
            _entries = entries;
            _size = size;
        }

        public object Current => _entries[_index].qname;
        public bool MoveNext() => ++_index < _size;
        public void Reset() => _index = -1;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The nested MyEnumerator class should be upgraded to implement CSharpGenericEnumerator<Object>
        Assert.Contains("CSharpGenericEnumerator<Object>", code);
        // It should NOT still implement only the non-generic CSharpEnumerator
        Assert.DoesNotContain("implements CSharpEnumerator", code);
    }

    [Fact]
    public void TopLevelICollectable_IteratorReturnTypeUpgraded()
    {
        var result = Convert(@"
using System.Collections;

public class MyCollection : ICollection
{
    public int Count => 0;
    public bool IsSynchronized => false;
    public object SyncRoot => null;
    public void CopyTo(Array array, int index) { }

    public IEnumerator GetEnumerator()
    {
        return new MyEnumerator();
    }
}

public class MyEnumerator : IEnumerator
{
    public object Current => null;
    public bool MoveNext() => false;
    public void Reset() { }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // iterator() return type should be CSharpGenericEnumerator<Object>, not CSharpEnumerator
        Assert.Contains("CSharpGenericEnumerator<Object> iterator()", code);
        // The body should use CSharpGenericEnumerator.fromEnumerator, not CSharpEnumerator.from
        Assert.DoesNotContain("CSharpEnumerator.from(", code);
    }

    [Fact(Skip = "Pending: yield method timing — iterator() not yet created when UpgradeIteratorReturnTypeForGenericIterable runs")]
    public void CollectionBaseInheritingClass_IteratorReturnTypeUpgraded()
    {
        // Reproduces XmlSchemas : CollectionBase, IEnumerable<XmlSchema> pattern
        // where the Java class extends CSharpCollectionBase (which implements CSharpIList)
        // but does not directly list CSharpICollection<Object> in its ImplementedTypes.
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class MySchemas : CollectionBase, IEnumerable<MySchema>
{
    public void Add(MySchema schema)
    {
        InnerList.Add(schema);
    }

    public new IEnumerator<MySchema> GetEnumerator()
    {
        return new MySchemaEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class MySchema { }

public class MySchemaEnumerator : IEnumerator<MySchema>
{
    private MySchemas _col;
    private int _index = -1;

    public MySchemaEnumerator(MySchemas col) { _col = col; }
    public MySchema Current => (MySchema)_col.InnerList[_index];
    object IEnumerator.Current => Current;
    public bool MoveNext() => ++_index < _col.Count;
    public void Reset() => _index = -1;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // iterator() return type should be CSharpGenericEnumerator, not CSharpEnumerator
        Assert.DoesNotContain("CSharpEnumerator iterator()", code);
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
