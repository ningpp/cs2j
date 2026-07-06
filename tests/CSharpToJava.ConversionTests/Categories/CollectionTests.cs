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

    [Theory]
    [MemberData(nameof(CollectionSnippets))]
    public void CollectionConversion(string description, string csharp, string marker)
    {
        var result = Convert(csharp);
        AssertConversion(result, marker);
    }

    [Fact]
    public void ListField_ConvertsToCSharpList()
    {
        var result = Convert("using System.Collections.Generic; class C { public List<int> Items; }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpList;", "public CSharpList<Integer> Items;");
    }

    [Fact]
    public void DictionaryField_ConvertsToCSharpDictionary()
    {
        var result = Convert("using System.Collections.Generic; class C { public Dictionary<string, int> Map; }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpDictionary;", "public CSharpDictionary<String, Integer> Map;");
    }

    [Fact]
    public void HashSetField_ConvertsToCSharpHashSet()
    {
        var result = Convert("using System.Collections.Generic; class C { public HashSet<int> Set; }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpHashSet;", "public CSharpHashSet<Integer> Set;");
    }

    [Fact]
    public void ListReturnType_ConvertsToCSharpList()
    {
        var result = Convert("using System.Collections.Generic; class C { public List<string> Get() { return new List<string>(); } }");
        AssertConversion(result, "public CSharpList<String> get() {", "new CSharpList<String>()");
    }

    [Fact]
    public void ListParameter_ConvertsToCSharpList()
    {
        var result = Convert("using System.Collections.Generic; class C { public void Add(List<int> items) { } }");
        AssertConversion(result, "public void add(CSharpList<Integer> items) {");
    }

    [Fact]
    public void DictionaryIteration_ConvertsToEntrySet()
    {
        var result = Convert("using System.Collections.Generic; class C { public int M(Dictionary<string,int> d) { int s=0; foreach (var kv in d) { s += kv.Value; } return s; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "CSharpDictionary");
        AssertJavaContains(result, "for (");
    }

    [Fact]
    public void ListAsIEnumerable_ConvertsToCSharpGenericIterable()
    {
        var result = Convert("using System.Collections.Generic; class C { public void M(IEnumerable<int> e) { foreach (var x in e) { var y = x; } } }");
        AssertConversion(result, "CSharpGenericIterable<Integer>", "for (int x : e)");
    }

    [Fact]
    public void StackField_ConvertsToCSharpStack()
    {
        var result = Convert("using System.Collections.Generic; class C { public Stack<int> S; }");
        AssertConversion(result, "public CSharpStack<Integer> S;");
    }

    [Fact]
    public void QueueField_ConvertsToCSharpQueue()
    {
        var result = Convert("using System.Collections.Generic; class C { public Queue<int> Q; }");
        AssertConversion(result, "public CSharpQueue<Integer> Q;");
    }

    [Fact]
    public void CollectionInitializerList_ConvertsToConstructor()
    {
        var result = Convert("using System.Collections.Generic; class C { public List<int> Items = new List<int> { 1, 2, 3 }; }");
        AssertConversion(result, "CSharpList<Integer>", "ArrayHelper.toList(1, 2, 3)");
    }

    [Fact]
    public void CollectionInitializerDictionary_ConvertsToPuts()
    {
        var result = Convert("using System.Collections.Generic; class C { public Dictionary<string,int> M = new Dictionary<string,int> { { \"a\", 1 }, { \"b\", 2 } }; }");
        AssertConversion(result, "CSharpDictionary<String, Integer>", "put(\"a\", 1)", "put(\"b\", 2)");
    }
}
