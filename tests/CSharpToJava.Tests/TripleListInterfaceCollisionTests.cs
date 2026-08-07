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

    [Fact]
    public async Task DualGenericPlusNonGenericIList_EmitsCompilableShape()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-triple-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(tempDir, "Test.cs"), Source);

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                tempDir, new ConversionOptions(), projectName: "TripleTest");

            var result = Assert.Single(results, r => r.FileName != null && r.FileName.Contains("CaptureLikeCollection"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
            var code = result.GeneratedCode ?? "";

            // The non-generic IList interface (CSharpGenericIList<Object>) must be
            // suppressed when typed list interfaces are present — its boolean
            // add(Object) / Object get(int) members clash with the typed ones.
            Assert.DoesNotContain("implements CSharpGenericIList<Object>", code);

            // Same-erasure accessor pairs must not both survive under one name:
            // exactly one get(int) declaration, and it must be the typed one.
            var declarations = CountOccurrences(code, " get(int ");
            Assert.True(declarations == 1,
                $"{declarations} get(int) declarations survived\n---Generated---\n{code}");
            Assert.DoesNotContain("Object get(int", code);

            // Non-generic IList members with Object parameters erase-clash with the
            // generic interface members and must be dropped.
            Assert.DoesNotContain("int add(Object", code);
            Assert.DoesNotContain("insert(int index, Object", code);

            // The iterable bridge must carry the typed element, not Object.
            Assert.DoesNotContain("Iterable<Object>", code);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
        }
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
}
