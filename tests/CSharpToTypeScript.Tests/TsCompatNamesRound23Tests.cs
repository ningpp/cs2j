using System.Text.RegularExpressions;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 23：Java 风味 CSharp* compat 名（io.github.ningpp.compat 面）在 TS 目标
/// 无类型/值面——CSharpGenericEnumerator/CSharpGenericIList/CSharpRandom/CSharpEnumerator/
/// CSharpQueue/CSharpLinkedList(Node)/CSharpSortedDict/CSharpKeyValuePair/CSharpArray/
/// CSharpObjQueue/CSharpGenericIterable → TS2304（233 行，TS2304 头号名字族）。
/// 修复：compat 运行时类（CsEnumerator/CsRandom/CsQueue/CsLinkedList/CsSortedDictionary/
/// CsArray）+ global.d.ts type+const 合并桩（接口契约供 implements；const 供值位
/// from()/new） + 生成类补齐桥接成员（getCurrent()/get()）。
/// </summary>
public class TsCompatNamesRound23Tests : TsConversionTestBase
{
    /// <summary>与真实工程 global.d.ts（NpmProjectPlan.GenerateGlobalTypeStub）同源镜像：
    /// CSharp* 名全部内联声明（测试 compat 桩无新类面）。</summary>
    private const string GlobalStub =
        """
        type Integer = number;
        type Long = bigint;
        type Double = number;
        type Float = number;
        type Short = number;
        type Byte = number;
        type Character = string;
        type CSharpList<T> = Array<T>;
        type CSharpStack<T> = Array<T>;
        type CSharpICollection<T> = Iterable<T>;
        type CSharpGenericComparer<T> = { compare(a: T, b: T): number };
        interface CSharpGenericEnumerator<T> {
            hasNext(): boolean;
            next(): T | null;
            moveNext(): boolean;
            getCurrent(): T | null;
        }
        declare const CSharpGenericEnumerator: { from<T>(source: Iterable<T> | IterableIterator<T> | CSharpGenericEnumerator<T>): CSharpGenericEnumerator<T> };
        interface CSharpGenericIList<T> {
            [Symbol.iterator](): Iterator<T>;
            add(item: T): boolean;
            remove(item: any): boolean;
            indexOf(item: any): number;
            insert(index: number, item: T): void;
            removeAt(index: number): void;
            size(): number;
            get(index: number): T;
            iterator(): CSharpGenericEnumerator<T> | IterableIterator<T>;
        }
        type CSharpEnumerator = CSharpGenericEnumerator<any>;
        declare const CSharpEnumerator: { from<T>(source: Iterable<T> | IterableIterator<T> | CSharpGenericEnumerator<T>): CSharpGenericEnumerator<T> };
        type CSharpRandom = { nextDouble(): number; next(bound: number): number };
        declare const CSharpRandom: { new(seed?: number): CSharpRandom };
        type CSharpQueue<T> = { enqueue(item: T): void; add(item: T): void; dequeue(): T; poll(): T | null; peek(): T; size(): number; isEmpty(): boolean; clear(): void; [Symbol.iterator](): Iterator<T> };
        declare const CSharpQueue: { new<T>(items?: Iterable<T>): CSharpQueue<T> };
        type CSharpLinkedList<T> = { addLast(value: T): CSharpLinkedListNode<T>; addFirst(node: CSharpLinkedListNode<T>): void; remove(node: CSharpLinkedListNode<T>): void; getFirst(): CSharpLinkedListNode<T>; getLast(): CSharpLinkedListNode<T>; size(): number; contains(value: T): boolean; length: number };
        declare const CSharpLinkedList: { new<T>(): CSharpLinkedList<T> };
        type CSharpLinkedListNode<T> = { getNext(): CSharpLinkedListNode<T>; getPrevious(): CSharpLinkedListNode<T>; getValue(): T; getList(): CSharpLinkedList<T> };
        declare const CSharpLinkedListNode: { new<T>(value: T): CSharpLinkedListNode<T> };
        type CSharpSortedDict<K, V> = { put(key: K, value: V): void; add(key: K, value: V): void; get(key: K): V; containsKey(key: K): boolean; remove(key: K): boolean; keySet(): K[]; values(): V[]; size(): number; clear(): void; iterator(): CSharpGenericEnumerator<CSharpKeyValuePair<K, V>>; [Symbol.iterator](): Iterator<CSharpKeyValuePair<K, V>> };
        declare const CSharpSortedDict: { new<K, V>(comparer?: { compare(a: K, b: K): number }): CSharpSortedDict<K, V> };
        type CSharpKeyValuePair<K, V> = { key: K; value: V; getKey(): K; getValue(): V };
        declare const CSharpKeyValuePair: { new<K, V>(key: K, value: V): CSharpKeyValuePair<K, V> };
        type CSharpGenericIterable<T> = Iterable<T>;
        type CSharpArray = { setValue(value: unknown, index: number): void; getValue(index: number): unknown };
        declare const CSharpArray: { of(inner: unknown[]): CSharpArray };
        type CSharpObjQueue = CSharpQueue<unknown>;
        declare const CSharpObjQueue: { new(): CSharpObjQueue };
        declare namespace System { function arraycopy(src: unknown, srcPos: number, dest: unknown, destPos: number, length: number): void; }
        declare namespace java { namespace util { class NoSuchElementException { constructor(message?: string); } } }
        interface Array<T> {
            get(index: number): T;
            set(index: number, value: T): void;
            add(value: T): void;
            addAll(values: Iterable<T>): void;
            remove(value: T): boolean;
            removeAt(index: number): void;
            insert(index: number, value: T): void;
            removeRange(start: number, end: number): void;
            size(): number;
            toArray<U = T>(mapper?: (n: number) => U[]): U[];
            iterator(): IterableIterator<T>;
            spliterator(): IterableIterator<T>;
            exists(predicate: (value: T) => boolean): boolean;
            ensureCapacity(minimumCapacity: number): void;
            peek(): T;
        }
        """;

    [Fact]
    public void GlobalStub_Declares_JavaFlavor_CSharp_Compat_Names()
    {
        var stub = NpmProjectFiles.GenerateGlobalTypeStub();
        Assert.Contains("interface CSharpGenericEnumerator<T>", stub);
        Assert.Contains("declare const CSharpGenericEnumerator: typeof import", stub);
        Assert.Contains("interface CSharpGenericIList<T>", stub);
        Assert.Contains("type CSharpEnumerator = import", stub);
        Assert.Contains("declare const CSharpEnumerator: typeof import", stub);
        Assert.Contains("type CSharpRandom = import", stub);
        Assert.Contains("declare const CSharpRandom: typeof import", stub);
        Assert.Contains("type CSharpQueue<T> = import", stub);
        Assert.Contains("declare const CSharpQueue: typeof import", stub);
        Assert.Contains("type CSharpLinkedList<T> = import", stub);
        Assert.Contains("type CSharpLinkedListNode<T> = import", stub);
        Assert.Contains("type CSharpSortedDict<K, V> = import", stub);
        Assert.Contains("declare const CSharpSortedDict: typeof import", stub);
        Assert.Contains("type CSharpKeyValuePair<K, V> = import", stub);
        Assert.Contains("declare const CSharpKeyValuePair: typeof import", stub);
        Assert.Contains("type CSharpGenericIterable<T> = Iterable<T>", stub);
        Assert.Contains("type CSharpArray = import", stub);
        Assert.Contains("declare const CSharpArray: typeof import", stub);
        Assert.Contains("type CSharpObjQueue = import", stub);
        Assert.Contains("declare const CSharpObjQueue: typeof import", stub);
    }

    [Fact]
    public void EndToEnd_Random_Maps_To_CSharpRandom_And_Compiles()
    {
        var result = Convert("""
using System;
public class Probe
{
    private Random rnd = new Random(3);
    public double NextDouble() { return rnd.NextDouble(); }
    public int Next(int bound) { return rnd.Next(bound); }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new CSharpRandom(3)", result.GeneratedCode);
        Assert.Contains("rnd.nextDouble()", result.GeneratedCode);
        Assert.Contains("rnd.next(bound)", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void EndToEnd_IEnumerator_Bridge_Compiles_And_Adapter_Asserts_Element()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Boxes : IEnumerable<int>
{
    private List<int> items = new List<int>();
    public IEnumerator<int> GetEnumerator() { return items.GetEnumerator(); }
    public int Sum()
    {
        int s = 0;
        foreach (int x in this) s = s + x;
        return s;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("__it.getCurrent() as number", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void EndToEnd_IEnumerator_Impl_Class_Gets_GetCurrent_Bridge()
    {
        var result = Convert("""
using System.Collections.Generic;
public class En : IEnumerator<int>
{
    public int Current { get { return 1; } }
    public bool MoveNext() { return false; }
    public void Reset() { }
    public void Dispose() { }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("implements CSharpGenericEnumerator<number>", result.GeneratedCode);
        Assert.Contains("public getCurrent(): number | null { return this.Current; }", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void EndToEnd_IList_Impl_Class_Gets_Get_Bridge()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Node
{
    public int Id { get; set; }
}
public class NodeCollection : IList<Node>
{
    private List<Node> nodes = new List<Node>();
    public Node this[int index] { get { return nodes[index]; } set { nodes[index] = value; } }
    public int Count { get { return nodes.Count; } }
    public bool IsReadOnly { get { return false; } }
    public void Add(Node item) { nodes.Add(item); }
    public void Clear() { nodes.Clear(); }
    public bool Contains(Node item) { return nodes.Contains(item); }
    public void CopyTo(Node[] array, int arrayIndex) { nodes.CopyTo(array, arrayIndex); }
    public IEnumerator<Node> GetEnumerator() { return nodes.GetEnumerator(); }
    public int IndexOf(Node item) { return nodes.IndexOf(item); }
    public void Insert(int index, Node item) { nodes.Insert(index, item); }
    public bool Remove(Node item) { return nodes.Remove(item); }
    public void RemoveAt(int index) { nodes.RemoveAt(index); }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("implements CSharpGenericIList<Node>", result.GeneratedCode);
        Assert.Contains("public get(index: number): Node { return this.get_Item(index); }", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void EndToEnd_LinkedList_Queue_SortedDictionary_KeyValuePair_Compile()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
public class Probe
{
    private LinkedList<int> list = new LinkedList<int>();
    private Queue<int> queue = new Queue<int>();
    private SortedDictionary<int, int> dict = new SortedDictionary<int, int>();
    private List<KeyValuePair<int, int>> pairs = new List<KeyValuePair<int, int>>();

    public void M(Random rnd)
    {
        list.AddLast(1);
        int v = list.First.Value;
        queue.Enqueue(v);
        queue.Dequeue();
        dict.Add(v, 1);
        int w = dict[v];
        KeyValuePair<int, int> pair = new KeyValuePair<int, int>(v, w);
        pairs.Add(pair);
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("CSharpLinkedList<number>", result.GeneratedCode);
        Assert.Contains("CSharpQueue<number>", result.GeneratedCode);
        Assert.Contains("CSharpSortedDict<number, number>", result.GeneratedCode);
        Assert.Contains("CSharpKeyValuePair<number, number>", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }
}
