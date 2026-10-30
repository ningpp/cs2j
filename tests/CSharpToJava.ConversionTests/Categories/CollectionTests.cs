using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# collection types (List, Dictionary, HashSet, Queue, Stack,
/// LinkedList, SortedList, SortedSet, ArrayList, BitArray, etc.) to the Java compat library.
/// </summary>
public class CollectionTests : ConversionTestBase
{
    public static IEnumerable<object[]> CollectionSnippets()
    {
        // (description, csharp snippet, expected Java marker)
        yield return new object[] { "List creation", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); } }", "CSharpList<Integer>" };
        yield return new object[] { "List add", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Add(1); } }", "l.add(1);" };
        yield return new object[] { "List count", "class C { public int M() { var l = new System.Collections.Generic.List<int>(); return l.Count; } }", "l.size()" };
        yield return new object[] { "List indexer get", "class C { public int M() { var l = new System.Collections.Generic.List<int>(); return l[0]; } }", "l.get(0)" };
        yield return new object[] { "List indexer set", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l[0] = 5; } }", "l.set(0, 5)" };
        yield return new object[] { "List contains", "class C { public bool M() { var l = new System.Collections.Generic.List<int>(); return l.Contains(1); } }", "l.contains(1)" };
        yield return new object[] { "List remove", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Remove(1); } }", "l.remove(" };
        yield return new object[] { "List clear", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Clear(); } }", "l.clear()" };
        yield return new object[] { "List addRange", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); var o = new System.Collections.Generic.List<int>(); l.AddRange(o); } }", "addRange" };
        yield return new object[] { "List insert", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Insert(0, 1); } }", "l.insert(0, 1)" };
        yield return new object[] { "List find", "class C { public int M() { var l = new System.Collections.Generic.List<int>(); return l.Find(x => x > 0); } }", "l.find(" };
        yield return new object[] { "List exists", "class C { public bool M() { var l = new System.Collections.Generic.List<int>(); return l.Exists(x => x > 0); } }", "l.stream().anyMatch(" };
        yield return new object[] { "List trueForAll", "class C { public bool M() { var l = new System.Collections.Generic.List<int>(); return l.TrueForAll(x => x > 0); } }", "l.stream().allMatch(" };
        yield return new object[] { "List foreach", "class C { public int M() { var l = new System.Collections.Generic.List<int>(); int s = 0; foreach (var x in l) { s += x; } return s; } }", "for (int x : l)" };
        yield return new object[] { "List toArray", "class C { public int[] M() { var l = new System.Collections.Generic.List<int>(); return l.ToArray(); } }", "toArray" };
        yield return new object[] { "List sort", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Sort(); } }", "Collections.sort(l)" };
        yield return new object[] { "List of string", "class C { public void M() { var l = new System.Collections.Generic.List<string>(); } }", "CSharpList<String>" };
        yield return new object[] { "Dictionary creation", "class C { public void M() { var d = new System.Collections.Generic.Dictionary<string, int>(); } }", "CSharpDictionary<String, Integer>" };
        yield return new object[] { "Dictionary put", "class C { public void M() { var d = new System.Collections.Generic.Dictionary<string, int>(); d[\"a\"] = 1; } }", "d.put(\"a\", 1)" };
        yield return new object[] { "Dictionary get", "class C { public int M() { var d = new System.Collections.Generic.Dictionary<string, int>(); return d[\"a\"]; } }", "d.get(\"a\")" };
        yield return new object[] { "Dictionary containsKey", "class C { public bool M() { var d = new System.Collections.Generic.Dictionary<string, int>(); return d.ContainsKey(\"a\"); } }", "d.containsKey(\"a\")" };
        yield return new object[] { "Dictionary tryGetValue", "class C { public bool M() { var d = new System.Collections.Generic.Dictionary<string, int>(); int v; return d.TryGetValue(\"a\", out v); } }", "d.containsKey(" };
        yield return new object[] { "Dictionary keys", "class C { public int M() { var d = new System.Collections.Generic.Dictionary<string, int>(); return d.Keys.Count; } }", "d.keySet().size()" };
        yield return new object[] { "Dictionary values", "class C { public int M() { var d = new System.Collections.Generic.Dictionary<string, int>(); return d.Values.Count; } }", "d.values()" };
        yield return new object[] { "Dictionary remove", "class C { public void M() { var d = new System.Collections.Generic.Dictionary<string, int>(); d.Remove(\"a\"); } }", "d.remove(\"a\")" };
        yield return new object[] { "Dictionary clear", "class C { public void M() { var d = new System.Collections.Generic.Dictionary<string, int>(); d.Clear(); } }", "d.clear()" };
        yield return new object[] { "HashSet creation", "class C { public void M() { var s = new System.Collections.Generic.HashSet<int>(); } }", "CSharpHashSet<Integer>" };
        yield return new object[] { "HashSet add", "class C { public void M() { var s = new System.Collections.Generic.HashSet<int>(); s.Add(1); } }", "s.add(1)" };
        yield return new object[] { "HashSet contains", "class C { public bool M() { var s = new System.Collections.Generic.HashSet<int>(); return s.Contains(1); } }", "s.contains(1)" };
        yield return new object[] { "HashSet remove", "class C { public void M() { var s = new System.Collections.Generic.HashSet<int>(); s.Remove(1); } }", "s.remove(" };
        yield return new object[] { "Queue creation", "class C { public void M() { var q = new System.Collections.Generic.Queue<int>(); } }", "CSharpQueue<Integer>" };
        yield return new object[] { "Queue enqueue", "class C { public void M() { var q = new System.Collections.Generic.Queue<int>(); q.Enqueue(1); } }", "q.enqueue(1)" };
        yield return new object[] { "Queue dequeue", "class C { public int M() { var q = new System.Collections.Generic.Queue<int>(); return q.Dequeue(); } }", "q.dequeue()" };
        yield return new object[] { "Queue peek", "class C { public int M() { var q = new System.Collections.Generic.Queue<int>(); return q.Peek(); } }", "q.peek()" };
        yield return new object[] { "Stack creation", "class C { public void M() { var s = new System.Collections.Generic.Stack<int>(); } }", "CSharpStack<Integer>" };
        yield return new object[] { "Stack push", "class C { public void M() { var s = new System.Collections.Generic.Stack<int>(); s.Push(1); } }", "s.push(1)" };
        yield return new object[] { "Stack pop", "class C { public int M() { var s = new System.Collections.Generic.Stack<int>(); return s.Pop(); } }", "s.pop()" };
        yield return new object[] { "Stack peek", "class C { public int M() { var s = new System.Collections.Generic.Stack<int>(); return s.Peek(); } }", "s.peek()" };
        yield return new object[] { "LinkedList creation", "class C { public void M() { var l = new System.Collections.Generic.LinkedList<int>(); } }", "CSharpLinkedList<Integer>" };
        yield return new object[] { "LinkedList addLast", "class C { public void M() { var l = new System.Collections.Generic.LinkedList<int>(); l.AddLast(1); } }", "l.addLast(1)" };
        yield return new object[] { "SortedList creation", "class C { public void M() { var l = new System.Collections.Generic.SortedList<int, string>(); } }", "CSharpSortedList<Integer, String>" };
        yield return new object[] { "SortedSet creation", "class C { public void M() { var s = new System.Collections.Generic.SortedSet<int>(); } }", "CSharpSortedSet<Integer>" };
        yield return new object[] { "ArrayList creation", "class C { public void M() { var l = new System.Collections.ArrayList(); } }", "CSharpArrayList" };
        yield return new object[] { "ArrayList add", "class C { public void M() { var l = new System.Collections.ArrayList(); l.Add(1); } }", "l.add(1)" };
        yield return new object[] { "BitArray creation", "class C { public void M() { var b = new System.Collections.BitArray(8); } }", "CSharpBitArray" };
        yield return new object[] { "BitArray get", "class C { public bool M() { var b = new System.Collections.BitArray(8); return b[0]; } }", "b.get(0)" };
        yield return new object[] { "BitArray set", "class C { public void M() { var b = new System.Collections.BitArray(8); b[0] = true; } }", "b.set(0, true)" };
        yield return new object[] { "Queue nonGeneric creation", "class C { public void M() { var q = new System.Collections.Queue(); } }", "CSharpObjQueue" };
        yield return new object[] { "Stack nonGeneric creation", "class C { public void M() { var s = new System.Collections.Stack(); } }", "CSharpObjStack" };
        yield return new object[] { "Hashtable creation", "class C { public void M() { var h = new System.Collections.Hashtable(); } }", "CSharpHashtable" };
        yield return new object[] { "List of custom type", "class Item { } class C { public void M() { var l = new System.Collections.Generic.List<Item>(); } }", "CSharpList<Item>" };
        yield return new object[] { "Dictionary nested value", "class C { public void M() { var d = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>(); } }", "CSharpDictionary<Integer, CSharpList<Integer>>" };
        yield return new object[] { "List capacity ctor", "class C { public void M() { var l = new System.Collections.Generic.List<int>(10); } }", "CSharpList<Integer>(10)" };
        yield return new object[] { "List addRange varargs", "class C { public void M() { var l = new System.Collections.Generic.List<int>(); l.Add(1); l.Add(2); l.Add(3); } }", "l.add(2)" };
        yield return new object[] { "HashSet unionWith", "class C { public void M() { var s = new System.Collections.Generic.HashSet<int>(); var o = new System.Collections.Generic.HashSet<int>(); s.UnionWith(o); } }", "s.unionWith(" };
        yield return new object[] { "Dictionary count", "class C { public int M() { var d = new System.Collections.Generic.Dictionary<string, int>(); return d.Count; } }", "d.size()" };
        yield return new object[] { "List count property", "class C { public int M() { var l = new System.Collections.Generic.List<int>(); l.Add(1); int c = l.Count; return c; } }", "l.size()" };
    }

    [Fact]
    public void SpanRefReturnIndexer_Assignment_UsesSetAccessor()
    {
        // Span<T>'s indexer is `ref T this[int]` (SetMethod == null). The
        // assignment must route through the element-access path, not the
        // auto-property backing-field path (which produced `this.this[]`).
        var result = Convert(
            "using System; class C { public void F(Span<ushort> s) { s[0] = (ushort)1; s[1] += 2; } }");

        AssertSuccess(result);
        AssertJavaContains(result, "s.set(0, ");
        AssertJavaContains(result, "s.set(1, s.get(1) + 2)");
        AssertJavaDoesNotContain(result, "this.this[]");
    }

    [Fact]
    public void ClassImplementingIList_AssignedToICollection_IsWrappedWithTypedAdapter()
    {
        // Bug A: a generated class implementing C# IList<T> renders as
        // `implements CSharpIList<T>` (the parallel compat hierarchy), which is
        // not assignable to a CSharpICollection<T> slot. The local-declaration
        // position must route through CSharpICollection.fromTyped (real chain:
        // CaptureCollectionTests' `ICollection<Capture> x = createCollection()`).
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;

class Capture { }

class CaptureList : IList<Capture>
{
    public Capture this[int index] { get { return null; } set { } }
    public int Count { get { return 0; } }
    public bool IsReadOnly { get { return true; } }
    public void Add(Capture item) { }
    public void Clear() { }
    public bool Contains(Capture item) { return false; }
    public void CopyTo(Capture[] array, int arrayIndex) { }
    public IEnumerator<Capture> GetEnumerator() { return null; }
    IEnumerator IEnumerable.GetEnumerator() { return null; }
    public int IndexOf(Capture item) { return -1; }
    public void Insert(int index, Capture item) { }
    public void RemoveAt(int index) { }
    public bool Remove(Capture item) { return false; }
}

class Host
{
    static CaptureList Create() { return new CaptureList(); }
    static void Use()
    {
        ICollection<Capture> collection = Create();
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "public class CaptureList implements CSharpIList<Capture>");
        AssertJavaContains(
            result,
            "CSharpICollection<Capture> collection = CSharpICollection.fromTyped(create());");
    }

    [Fact]
    public void ClassImplementingICollection_AssignedToICollection_IsNotWrapped()
    {
        // Negative: a class that directly implements C# ICollection<T> is
        // emitted as `implements CSharpICollection<T>` already, and a value
        // statically typed ICollection<T> maps to CSharpICollection<T>; neither
        // may be wrapped again (the adapter is not idempotent in text).
        var result = Convert("""
using System.Collections;
using System.Collections.Generic;

class Bag : ICollection<int>
{
    public void Add(int item) { }
    public bool Contains(int item) { return false; }
    public bool Remove(int item) { return false; }
    public void CopyTo(int[] array, int arrayIndex) { }
    public int Count { get { return 0; } }
    public bool IsReadOnly { get { return true; } }
    public IEnumerator<int> GetEnumerator() { return null; }
    IEnumerator IEnumerable.GetEnumerator() { return null; }
}

class Host
{
    static Bag Create() { return new Bag(); }
    static void Use(ICollection<int> source)
    {
        ICollection<int> fromClass = Create();
        ICollection<int> fromInterface = source;
    }
}
""");

        AssertSuccess(result);
        AssertJavaContains(result, "public class Bag implements CSharpICollection<Integer>");
        AssertJavaContains(result, "CSharpICollection<Integer> fromClass = create();");
        AssertJavaContains(result, "CSharpICollection<Integer> fromInterface = source;");
        AssertJavaDoesNotContain(result, "CSharpICollection.fromTyped(");
    }

    [Fact]
    public void HashSetFromPrimitiveArray_ArgumentIsAdaptedToIterable()
    {
        // Bug B: a C# array is IEnumerable<T> but a Java array implements no
        // collection interface; CSharpHashSet has no char[] constructor, so the
        // constructor argument needs the primitive-array iterable adapter.
        var result = Convert(
            "using System.Collections.Generic; class C { void M(char[] a) { var s = new HashSet<char>(a); } }");

        AssertSuccess(result);
        AssertJavaContains(
            result,
            "new CSharpHashSet<Character>(CSharpGenericIterable.<Character>fromAnyTyped(a))");
        AssertJavaDoesNotContain(result, "new CSharpHashSet<Character>(a)");
    }

    [Fact]
    public void HashSetFromReferenceArray_ArgumentUsesLiveArrayView()
    {
        // Same constructor coercion for reference arrays: the live array view
        // is a java.util.Collection, which the CSharpHashSet constructor accepts.
        var result = Convert(
            "using System.Collections.Generic; class C { void M(string[] a) { var s = new HashSet<string>(a); } }");

        AssertSuccess(result);
        AssertJavaContains(result, "new CSharpHashSet<String>(ArrayHelper.asListView(a))");
        AssertJavaDoesNotContain(result, "new CSharpHashSet<String>(a)");
    }
}
