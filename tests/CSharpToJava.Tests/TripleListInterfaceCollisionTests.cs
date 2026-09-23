using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a class implementing IList&lt;T&gt; + IReadOnlyList&lt;T&gt; + the
/// non-generic IList compiles after project-level conversion. The non-generic
/// IList maps to CSharpGenericIList&lt;Object&gt; whose boolean add(Object) /
/// Object get(int) members clash with the typed members and with the explicit
/// int IList.Add implementation (javac: wrong return type / cannot implement /
/// duplicate same-erasure methods). Reproduces CaptureCollection/GroupCollection.
/// </summary>
public class TripleListInterfaceCollisionTests
{
    private const string Source = @"
using System.Collections;
using System.Collections.Generic;

public class CaptureLike
{
    public string Name => """";
}

public class CaptureLikeCollection : IList<CaptureLike>, IReadOnlyList<CaptureLike>, IList
{
    public int Count => 0;
    public bool IsReadOnly => true;
    public CaptureLike this[int index] => null;

    public IEnumerator GetEnumerator() => new Enumerator(this);
    IEnumerator<CaptureLike> IEnumerable<CaptureLike>.GetEnumerator() => new Enumerator(this);

    int IList<CaptureLike>.IndexOf(CaptureLike item) => -1;
    void IList<CaptureLike>.Insert(int index, CaptureLike item) => throw new System.NotSupportedException();
    void IList<CaptureLike>.RemoveAt(int index) => throw new System.NotSupportedException();
    CaptureLike IList<CaptureLike>.this[int index] { get => null; set => throw new System.NotSupportedException(); }

    void ICollection<CaptureLike>.Add(CaptureLike item) => throw new System.NotSupportedException();
    void ICollection<CaptureLike>.Clear() => throw new System.NotSupportedException();
    bool ICollection<CaptureLike>.Contains(CaptureLike item) => false;
    void ICollection<CaptureLike>.CopyTo(CaptureLike[] array, int arrayIndex) { }
    bool ICollection<CaptureLike>.Remove(CaptureLike item) => throw new System.NotSupportedException();

    int IList.Add(object value) => throw new System.NotSupportedException();
    bool IList.Contains(object value) => false;
    int IList.IndexOf(object value) => -1;
    void IList.Insert(int index, object value) => throw new System.NotSupportedException();
    void IList.Remove(object value) => throw new System.NotSupportedException();
    void IList.RemoveAt(int index) => throw new System.NotSupportedException();
    bool IList.IsFixedSize => true;
    object IList.this[int index] { get => null; set => throw new System.NotSupportedException(); }

    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    void ICollection.CopyTo(System.Array array, int index) { }

    private sealed class Enumerator : IEnumerator<CaptureLike>
    {
        private readonly CaptureLikeCollection _collection;
        private int _index = -1;

        internal Enumerator(CaptureLikeCollection collection) { _collection = collection; }

        public bool MoveNext() { return ++_index < _collection.Count; }
        public CaptureLike Current => _collection[_index];
        object IEnumerator.Current => Current;
        void IEnumerator.Reset() => _index = -1;
        void System.IDisposable.Dispose() { }
    }
}";

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
}
