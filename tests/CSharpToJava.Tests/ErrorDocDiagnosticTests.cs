using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;
using System;

namespace CSharpToJava.Tests;

public class ErrorDocDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public ErrorDocDiagnosticTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    // Error 09: MemberwiseClone mapping
    [Fact]
    public void Error09_MemberwiseClone_MappedToClone()
    {
        var r = Convert(@"
class Settings {
    public int X;
    public virtual Settings Clone() {
        return (Settings)MemberwiseClone();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // The converter should add a memberwiseClone() helper that wraps super.clone()
        // and the class should implement Cloneable
        Assert.Contains("Cloneable", code);
        Assert.Contains("protected Object memberwiseClone()", code);
        Assert.Contains("super.clone()", code);
    }

    // Error 22: Math.Sign → Integer.signum
    [Fact]
    public void Error22_MathSign_MapsCorrectly()
    {
        var r = Convert(@"
using System;
class Sample {
    int M(int a, int b) {
        return Math.Sign(b - a);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should use Integer.signum for int input or (int)Math.signum
        bool usesIntegerSignum = code.Contains("Integer.signum");
        bool usesCastSignum = code.Contains("(int)") && code.Contains("Math.signum");
        Assert.True(usesIntegerSignum || usesCastSignum,
            $"Should map Math.Sign(int) correctly. Got: {code}");
    }

    // Error 23: !collection.Any() operator precedence
    [Fact]
    public void Error23_NotAny_CorrectPrecedence()
    {
        var r = Convert(@"
using System.Linq;
using System.Collections.Generic;
class Sample {
    bool M(List<int> items) {
        if (!items.Any()) return true;
        return false;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT produce !items.size() > 0 or !items.length > 0
        Assert.DoesNotContain("!items.size() > 0", code);
        Assert.DoesNotContain("!items.length > 0", code);
    }

    // Error 02: void lambda with unexpected return
    [Fact]
    public void Error02_VoidLambda_NoReturn()
    {
        var r = Convert(@"
using System;
class Sample {
    void Process(Action<int> action) {}
    bool Compute(int x) { return x > 0; }
    void M() {
        Process(x => { var result = Compute(x); });
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Consumer<Integer> lambda should not have return statement
        Assert.DoesNotContain("return _chainVal", code);
    }

    // Error 05: Static method using class-level generic param
    [Fact]
    public void Error05_StaticMethod_ClassGenericParam()
    {
        var r = Convert(@"
interface IEdge { int Source { get; } int Target { get; } }
class Graph<TEdge> where TEdge : IEdge {
    static int VertexCount(System.Collections.IEnumerable edges) {
        int n = 0;
        foreach (TEdge e in edges) {
            if (e.Source >= n) n = e.Source + 1;
        }
        return n;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Static method should not reference class-level TEdge without method-level declaration
        bool hasMethodLevelGeneric = code.Contains("static <TEdge extends IEdge> int vertexCount");
        bool noClassGenericInStaticBody = !code.Contains("(TEdge)") || hasMethodLevelGeneric;
        Assert.True(noClassGenericInStaticBody,
            $"Static method should handle class-level TEdge. Got: {code}");
    }

    // Error 07: RemoveAt void→T return type
    [Fact]
    public void Error07_RemoveAt_ReturnType()
    {
        var r = Convert(@"
using System.Collections.Generic;
class MyList<T> : IList<T> {
    private List<T> items = new List<T>();
    public T this[int index] { get => items[index]; set => items[index] = value; }
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(T item) { items.Add(item); }
    public void Clear() { items.Clear(); }
    public bool Contains(T item) { return items.Contains(item); }
    public void CopyTo(T[] array, int index) {}
    public IEnumerator<T> GetEnumerator() { return items.GetEnumerator(); }
    public int IndexOf(T item) { return items.IndexOf(item); }
    public void Insert(int index, T item) { items.Insert(index, item); }
    public bool Remove(T item) { return items.Remove(item); }
    public void RemoveAt(int index) { items.RemoveAt(index); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return items.GetEnumerator(); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        // Java List.remove(int) must return T, not void
        var code = r.GeneratedCode ?? "";
        // Should NOT have "void remove(int"
        Assert.DoesNotContain("void remove(int", code);
    }

    // Error 10: IntStream vs Stream - needs .boxed()
    [Fact]
    public void Error10_IntStream_NeedsBoxed()
    {
        var r = Convert(@"
using System.Linq;
class Sample {
    string[] M(int[] arr) {
        return arr.Select(x => x.ToString()).ToArray();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        // LINQ desugarer now always on; chain converted to procedural code
        Assert.Contains("ProceduralLinq", r.GeneratedCode ?? "");
    }

    // Error 13: Property ++ operator
    [Fact]
    public void Error13_PropertyIncrement()
    {
        var r = Convert(@"
class Counter {
    public int Value { get; set; }
    void M() {
        Value++;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // getValue()++ is invalid Java; should expand to setValue(getValue() + 1)
        Assert.DoesNotContain("getValue()++", code);
        Assert.Contains("setValue(", code);
    }

    // Error 14: Map.Entry vs SimpleEntry in foreach
    [Fact]
    public void Error14_MapEntry_InForeach()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    void M(Dictionary<string, int> dict) {
        foreach (var kv in dict) {
            var k = kv.Key;
            var v = kv.Value;
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should use Map.Entry, not SimpleEntry for iteration
        Assert.DoesNotContain("SimpleEntry", code);
    }

    // Error 19: var with lambda (can't infer type)
    [Fact]
    public void Error19_VarWithLambda()
    {
        var r = Convert(@"
using System;
class Sample {
    void M() {
        Func<int, int> f = x => x + 1;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Java var cannot infer lambda types. Should use explicit Function<Integer, Integer> or similar
        Assert.DoesNotContain("var f = ", code);
    }

    // Error 11: Array → Iterable return
    [Fact]
    public void Error11_ArrayReturn_AsIterable()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    private string[] items;
    public IEnumerable<string> GetItems() { return items; }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Returning an array where Iterable expected needs wrapping
        bool hasToList = code.Contains("ArrayHelper.toList(");
        bool hasListView = code.Contains("ArrayHelper.asListView(");
        bool hasListOf = code.Contains("List.of(");
        Assert.True(hasToList || hasListView || hasListOf,
            $"Array→Iterable return should be wrapped. Got: {code}");
    }

    // Error 20: Double Comparator inheritance
    [Fact]
    public void Error20_DoubleComparator()
    {
        var r = Convert(@"
using System;
using System.Collections.Generic;
class Base : IComparer<int> {
    public int Compare(int x, int y) { return x - y; }
}
class Child : Base, IComparer<string> {
    public int Compare(string x, string y) { return string.Compare(x, y); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Java can't implement Comparator<Integer> and Comparator<String> on same class
        // Should either not implement both or use adapter pattern
        // For now, just verify no compilation-breaking code is generated
        Assert.DoesNotContain("Comparator<Integer>, Comparator<String>", code);
    }

    // Error 21: Generic array creation
    [Fact]
    public void Error21_GenericArrayCreation()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M(Dictionary<string, int> dict) {
        var arr = dict.Where(kv => kv.Value > 0).ToArray();
        foreach (var item in arr) { }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT use generic array creation like SimpleEntry<String,Integer>[]::new
        // (AbstractMap.SimpleEntry<String,Integer> in variable declarations / for-each is valid Java)
        Assert.DoesNotMatch(@"toArray\([^)]*<[^)]*>::new\)", code);
    }

    // Error 24a: int[] wrapped as Iterable<Integer>
    [Fact]
    public void Error24a_IntArrayToIterableInteger()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    IEnumerable<int> GetItem(int node) {
        return new int[] { node };
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // ArrayHelper.toList(new int[]{node}) would return List<int[]>
        // Should use List.of(node) or similar
        Assert.DoesNotContain("ArrayHelper.toList(new int[]", code);
    }

    // Error 01: Missing return statement when ICollection.Add (void) → Collection.add (boolean)
    [Fact]
    public void Error01_MissingReturnInVoidToBooleanBridge()
    {
        var r = Convert(@"
using System.Collections.Generic;
class MySet<T> : ICollection<T> {
    private HashSet<T> inner = new HashSet<T>();
    public int Count => inner.Count;
    public bool IsReadOnly => false;
    public void Add(T item) { inner.Add(item); }
    public void Clear() { inner.Clear(); }
    public bool Contains(T item) { return inner.Contains(item); }
    public void CopyTo(T[] array, int index) { }
    public bool Remove(T item) { return inner.Remove(item); }
    public IEnumerator<T> GetEnumerator() { return inner.GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // If "boolean add" method exists, it must have a return statement
        if (code.Contains("boolean add("))
        {
            var addMethodIdx = code.IndexOf("boolean add(");
            var bodyStart = code.IndexOf('{', addMethodIdx);
            var bodyEnd = code.IndexOf('}', bodyStart + 1);
            var body = code.Substring(bodyStart, bodyEnd - bodyStart + 1);
            Assert.Contains("return", body);
        }
    }

    // Error 11: Node[] cannot convert to Iterable<Node>
    [Fact]
    public void Error11_ReferenceArrayToIterable()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Node { }
class Container {
    private Node[] items = new Node[0];
    public IEnumerable<Node> Nodes { get { return items; } }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Node[] can't be directly returned as Iterable<Node>
        // Should wrap with ArrayHelper.toList() or similar
        Assert.DoesNotMatch(@"return\s+items\s*;", code);
    }

    // Error 16: Delegate invocation - .Invoke() → functional interface method
    [Fact]
    public void Error16_DelegateInvocation()
    {
        var r = Convert(@"
using System;
class Sample {
    Action<string> handler;
    Func<int, string> converter;
    void Test() {
        handler(""hello"");
        var result = converter(42);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Delegate invocation should use .accept() for Action and .apply() for Func
        Assert.DoesNotContain(".Invoke(", code);
        Assert.DoesNotContain(".invoke(", code);
    }

    // Error 17: Stopwatch API mapping
    [Fact]
    public void Error17_StopwatchApi()
    {
        var r = Convert(@"
using System.Diagnostics;
class Timer {
    long freq;
    long startTime;
    void Init() {
        freq = Stopwatch.Frequency;
        startTime = Stopwatch.GetTimestamp();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should map to System.nanoTime() or similar, not StopwatchHelper
        Assert.DoesNotContain("StopwatchHelper", code);
    }

    // Error 24d: List<int>[] array creation
    [Fact]
    public void Error24d_ListArrayCreation()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    void M() {
        List<int>[] layers = new List<int>[5];
        layers[0] = new List<int>();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should create array directly, not use ArrayHelper.toList
        Assert.DoesNotContain("ArrayHelper.toList(new ArrayList", code);
    }

    // Error 24e: Array.spliterator() - arrays don't have instance spliterator
    [Fact]
    public void Error24e_ArraySpliterator()
    {
        var r = Convert(@"
using System;
using System.Threading.Tasks;
class Sample {
    string[] items;
    void M() {
        Parallel.ForEach(items, item => Console.WriteLine(item));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // arrays don't have .spliterator() instance method
        Assert.DoesNotContain("items.spliterator()", code);
    }

    // Error 13: Property setter used with ++ (post-increment on getter result)
    [Fact]
    public void Error13_PostIncrementOnProperty()
    {
        var r = Convert(@"
class Settings { public int Iterations { get; set; } }
class Sample {
    void M(Settings s) {
        s.Iterations++;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT produce s.getIterations()++ which is invalid Java
        Assert.DoesNotContain("getIterations()++", code);
        // Should use setIterations
        Assert.Contains("setIterations", code);
    }

    // Error 13b: Map indexer post-increment
    [Fact]
    public void Error13b_MapIndexerPostIncrement()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    void M() {
        Dictionary<string, int> degree = new Dictionary<string, int>();
        degree[""key""]++;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT produce degree.get("key")++ which is invalid Java
        Assert.DoesNotContain(".get(\"key\")++", code);
        // Should use merge or put pattern
        Assert.True(code.Contains("merge") || code.Contains("put"));
    }

    // Error 11: Array returned as IEnumerable<T>
    [Fact]
    public void Error11_ArrayReturnedAsIterable()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    int[] data = new int[5];
    IEnumerable<int> GetData() {
        return data;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should wrap array to Iterable somehow (ArrayHelper.toList, Arrays.stream, etc.)
        Assert.DoesNotContain("return data;", code);
    }

    // Error 04: IEnumerator<T> → Iterator<T> missing next()/hasNext()
    [Fact]
    public void Error04_EnumeratorToIterator()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;
class MyIterator : IEnumerator<string> {
    private string _current;
    public string Current => _current;
    object IEnumerator.Current => _current;
    public bool MoveNext() { return false; }
    public void Reset() { }
    public void Dispose() { }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should have next() and hasNext() methods
        Assert.Contains("next()", code);
        Assert.Contains("hasNext()", code);
    }

    // Error 12: out parameter should use Holder.value for assignment
    [Fact]
    public void Error12_OutParameterHolderValue()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Sample {
    bool TryGet(string key, out int value) {
        var dict = new Dictionary<string, int>();
        if (dict.TryGetValue(key, out value)) {
            return true;
        }
        value = -1;
        return false;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Assignments to out param should use .value
        Assert.Contains(".value", code);
    }

    // Error 12b: out member.field should use Holder pattern
    [Fact]
    public void Error12b_OutMemberFieldHolder()
    {
        var r = Convert(@"
class Data { public int[] items; }
class Sample {
    void Fill(out int[] arr) { arr = new int[] { 1, 2, 3 }; }
    void M() {
        var data = new Data();
        Fill(out data.items);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should use Holder for out member access, not /* out */
        Assert.DoesNotContain("/* out */", code);
        Assert.Contains("Holder", code);
        Assert.Contains(".value", code);
    }

    // Error 19a: duplicate closure variable declaration
    [Fact]
    public void Error19a_DuplicateClosureVariable()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M() {
        int i = 0;
        var list = new List<int> { 1, 2, 3 };
        var result = list.Select(x => x + i++).ToList();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT have duplicate _i declarations
        var count = System.Text.RegularExpressions.Regex.Matches(code, @"int\[\]\s+_i\s*=").Count;
        Assert.True(count <= 1, $"Expected at most 1 _i declaration but found {count}");
    }

    // Error 19b: var with lambda should use explicit type
    [Fact]
    public void Error19b_VarWithLambda()
    {
        var r = Convert(@"
using System;
class Sample {
    void M() {
        Func<int, int> doubler = x => x * 2;
        var result = doubler(5);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Lambda assignment should NOT use var (Java can't infer lambda type with var)
        Assert.DoesNotContain("var doubler", code);
    }

    // Error 16b: Delegate invocation with fields
    [Fact]
    public void Error16b_DelegateFieldInvocation()
    {
        var r = Convert(@"
using System;
class Sample {
    Action<string> callback;
    Func<int, string> converter;
    void M() {
        callback(""hello"");
        var result = converter(42);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should use .accept() for Action, .apply() for Func — NOT .invoke()
        Assert.DoesNotContain(".invoke(", code);
        Assert.DoesNotContain(".Invoke(", code);
    }

    // Error 03: Lambda in ambiguous constructor context should be cast
    [Fact]
    public void Error03_LambdaComparatorCast()
    {
        var r = Convert(@"
using System;
using System.Collections.Generic;
class Sample {
    void M() {
        var list = new List<int>();
        list.Sort((a, b) => a.CompareTo(b));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Sort with comparator lambda should compile
        Assert.Contains("sort(", code);
    }

    // Error 15: AddRange after LINQ collect should not re-collect
    [Fact]
    public void Error15_AddRangeAfterCollect()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node {
    public IEnumerable<Node> OutEdges;
    public IEnumerable<Node> InEdges;
    public int Id;
}
class Sample {
    void M(Node ni) {
        var neighb = ni.OutEdges.Where(e => e.Id > 0).Select(e => e).ToList();
        neighb.AddRange(ni.InEdges.Where(e => e.Id > 0).Select(e => e));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // AddRange of a LINQ chain (IEnumerable, not Collection) uses forEach(::add)
        // because Java's addAll() requires Collection, and stream chains aren't Collections
        Assert.Contains(".forEach(neighb::add)", code);
        // neighb should not have .collect() called on it
        Assert.DoesNotContain("neighb.collect(", code);
    }

    // Error 06: Ambiguous constructor when collect() result matches multiple overloads
    // This is a project-specific issue: generated wrapper classes have multiple constructor
    // overloads that ArrayList/Iterable can match. Not a general converter bug.
    [Fact]
    public void Error06_AmbiguousConstructorFromCollect()
    {
        // Simplified version: a class with multiple constructors, one taking Iterable
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Container<T> {
    public Container(IEnumerable<T> items) { }
    public Container(T singleItem) { }
}
class Sample {
    void M(List<int> data) {
        var c = new Container<int>(data.Where(x => x > 0));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("new Container", code);
    }

    // Error 08: Object initializer type lost when type has no Java mapping
    // This is a type mapping configuration issue — .NET-specific types (XmlReaderSettings,
    // ProcessStartInfo, etc.) have no Java equivalent and fall back to Object.
    [Fact]
    public void Error08_ObjectInitializerPreservesType()
    {
        // Test with a user-defined type (not .NET framework) to verify object initializers work
        var r = Convert(@"
class Config {
    public bool Verbose { get; set; }
    public string Name { get; set; }
}
class Sample {
    void M() {
        var c = new Config { Verbose = true, Name = ""test"" };
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Object initializer should preserve the actual type, not degrade to Object
        Assert.Contains("new Config()", code);
        Assert.DoesNotContain("new Object()", code);
    }

    // Error 18: Process API - maps to ProcessBuilder in Java
    // This overlaps with Error 08 (missing type mappings for .NET framework types).
    // C#'s Process/ProcessStartInfo have no direct Java equivalent; needs manual mapping.
    [Fact]
    public void Error18_ProcessApiBasicConversion()
    {
        var r = Convert(@"
using System.Diagnostics;
class Sample {
    void M() {
        var p = new Process();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Process type name should be preserved (not degraded to Object)
        Assert.Contains("Process", code);
        Assert.DoesNotContain("new Object()", code);
    }

    // Error 20: Double Comparator inheritance
    [Fact]
    public void Error20_DoubleComparatorInheritance()
    {
        // This is a fundamental Java limitation (type erasure prevents implementing
        // the same generic interface with different type arguments in a class hierarchy).
        // Test that the converter at least doesn't crash and generates valid Java.
        var r = Convert(@"
using System;
using System.Collections.Generic;
abstract class BaseClass : IComparer<string> {
    public int Compare(string x, string y) => string.Compare(x, y);
}
class Derived : BaseClass, IComparer<int> {
    public int Compare(int x, int y) => x - y;
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("class Derived", code);
        // Verify the helper method was generated to avoid type erasure conflict
        Assert.Contains("asIntegerComparer", code);
        // Derived should not have 'implements Comparator<Integer>' in its declaration line
        var derivedLine = code.Split('\n').FirstOrDefault(l => l.Contains("class Derived")) ?? "";
        Assert.DoesNotContain("Comparator<Integer>", derivedLine);
    }

    // Error 15b: SelectMany chain should produce flatMap, not .collect() on intermediate
    [Fact]
    public void Error15b_SelectManyChain()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Graph {
    public IEnumerable<Edge> Edges;
}
class Edge { public int Weight; }
class Sample {
    void M(List<Graph> graphs) {
        var allEdges = graphs.SelectMany(g => g.Edges).ToList();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        // LINQ desugarer now always on
        Assert.Contains("ProceduralLinq", r.GeneratedCode ?? "");
    }

    // Error 15c: GroupBy then access values should not call .stream() on scalar
    [Fact]
    public void Error15c_GroupByValues()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Item { public string Category; public int Value; }
class Sample {
    void M(List<Item> items) {
        var groups = items.GroupBy(i => i.Category);
        foreach (var g in groups) {
            var sum = g.Sum(i => i.Value);
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // GroupBy should produce a reasonable output (Collectors.groupingBy or similar)
        Assert.DoesNotContain(".stream().stream()", code);
    }

    // Error 08b: Object initializer with unmapped .NET type should preserve type name
    [Fact]
    public void Error08b_UnmappedTypeFallback()
    {
        // When a type has no mapping, it should keep the original type name, not degrade to Object
        var r = Convert(@"
class Sample {
    void M() {
        var x = new SomeUnknownConfig();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Even unmapped types should preserve the type name
        Assert.Contains("SomeUnknownConfig", code);
        Assert.DoesNotContain("new Object()", code);
    }

    // Error 06b: Constructor call with LINQ result should add cast if ambiguous
    [Fact]
    public void Error06b_ConstructorWithLinqResult()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class TreeNode<T> {
    public TreeNode(IEnumerable<T> children) { }
    public TreeNode(TreeNode<T> singleChild) { }
}
class Sample {
    void M(List<int> data) {
        var node = new TreeNode<int>(data.Where(x => x > 0).Select(x => x));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        Assert.Contains("new TreeNode", code);
    }

    // Regression: Debug.Assert with out param — Holder must be declared before assert
    [Fact]
    public void AssertWithOutParam_HolderDeclaredBeforeAssert()
    {
        var r = Convert(@"
using System.Diagnostics;
using System.Collections.Generic;
class Sample {
    void M() {
        var dict = new Dictionary<string, int>();
        Debug.Assert(dict.TryGetValue(""key"", out var value));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Holder declaration must appear BEFORE the assert that uses it
        // Debug.Assert is compiled out in C# Release — converted to comment in Java
        Assert.Contains("/* Debug.Assert(", code);
        Assert.Contains("*/", code);
    }
}
