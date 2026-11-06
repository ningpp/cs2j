using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that an enumerator implementing IEnumerator&lt;T&gt; for two different T (plus the
/// explicit object Current) does not emit duplicate getCurrent() methods.
/// Reproduces GroupCollection.Enumerator: javac "method getCurrent() is already defined".
/// </summary>
public class DualGenericEnumeratorCurrentCollisionTests
{
    [Fact]
    public void DualIEnumeratorT_NoDuplicateGetCurrent()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class Item { public string Name => """"; }

public class ItemCollection : IEnumerable<Item>
{
    public int Count => 0;
    public Item this[int index] => null;
    public IEnumerator<Item> GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Enumerator : IEnumerator<Item>, IEnumerator<KeyValuePair<string, Item>>
    {
        private readonly ItemCollection _collection;
        private int _index;

        internal Enumerator(ItemCollection collection)
        {
            _collection = collection;
            _index = -1;
        }

        public bool MoveNext()
        {
            int size = _collection.Count;
            if (_index >= size) return false;
            _index++;
            return _index < size;
        }

        public Item Current
        {
            get
            {
                if (_index < 0 || _index >= _collection.Count)
                    throw new System.InvalidOperationException();
                return _collection[_index];
            }
        }

        KeyValuePair<string, Item> IEnumerator<KeyValuePair<string, Item>>.Current
        {
            get
            {
                if (_index < 0 || _index >= _collection.Count)
                    throw new System.InvalidOperationException();
                Item value = _collection[_index];
                return new KeyValuePair<string, Item>(value.Name, value);
            }
        }

        object IEnumerator.Current => Current;

        void IEnumerator.Reset() => _index = -1;
        void System.IDisposable.Dispose() { }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        var count = 0;
        var index = 0;
        while ((index = code.IndexOf("getCurrent()", index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "getCurrent()".Length;
        }
        // One declaration may remain; duplicate declarations (or a self-recursive
        // Object bridge) are invalid Java.
        var declarations = 0;
        index = 0;
        while ((index = code.IndexOf(" getCurrent() {", index, System.StringComparison.Ordinal)) >= 0)
        {
            declarations++;
            index += " getCurrent() {".Length;
        }
        Assert.True(declarations <= 1, $"found {declarations} getCurrent() declarations\n---Generated---\n{code}");
        Assert.DoesNotContain("Object getCurrent() { return getCurrent(); }", code.Replace("\r", "").Replace("\n", ""));
    }

    /// <summary>
    /// SuccEnumerator 形态：泛型 Current 是隐式实现，非泛型 IEnumerator.Current 是
    /// 显式实现且体不同（抛 NotImplementedException）。两体不能合并：非泛型槽必须
    /// 保留自己的实现体（getCurrentNonGeneric），不能被泛型实现静默替换。
    /// </summary>
    [Fact]
    public void ExplicitNonGenericCurrentBodyIsPreserved()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class Item { public int Value; }

public class Enumerator : IEnumerator<int>
{
    private IEnumerator _edges;

    public Enumerator(IEnumerator edges) { _edges = edges; }

    public bool MoveNext() => _edges.MoveNext();
    public void Reset() { _edges.Reset(); }
    public void Dispose() { }

    public int Current => ((Item)_edges.Current).Value;

    object IEnumerator.Current => throw new System.NotImplementedException();
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = (result.GeneratedCode ?? "").Replace("\r", "");

        Assert.Contains("getCurrentNonGeneric()", code);
        Assert.Contains("throw new UnsupportedOperationException()", code);
        Assert.Contains("Integer getCurrent()", code);
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
