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
        var code = r.GeneratedCode ?? "";
        // int[] produces IntStream; .map() on IntStream returns IntStream which can't hold String.
        // Need .boxed() or .mapToObj()
        bool hasBoxed = code.Contains(".boxed()");
        bool hasMapToObj = code.Contains(".mapToObj(");
        Assert.True(hasBoxed || hasMapToObj,
            $"Should convert IntStream to Stream. Got: {code}");
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
        bool hasAsList = code.Contains("Arrays.asList(");
        bool hasListOf = code.Contains("List.of(");
        Assert.True(hasAsList || hasListOf,
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
        // Arrays.asList(new int[]{node}) would return List<int[]>
        // Should use List.of(node) or similar
        Assert.DoesNotContain("Arrays.asList(new int[]", code);
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
        // Should wrap with Arrays.asList() or similar
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
        // Should create array directly, not use Arrays.asList
        Assert.DoesNotContain("Arrays.asList(new ArrayList", code);
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
        // Should wrap array to Iterable somehow (Arrays.asList, Arrays.stream, etc.)
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
}
