# C# Collection Framework Java Compat Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a complete Java compatibility layer for C# collection frameworks (System.Collections + System.Collections.Generic) using thin adapters over Java standard collections, with C# reflection-generated test oracles.

**Architecture:** Thin adapter pattern — Java interfaces mirror C# interfaces, concrete classes wrap Java standard collections. A C# reflection tool generates API signatures and test data. Java tests validate against C# oracle output.

**Tech Stack:** Java 25 (Maven), C# .NET 10 (reflection tool), JUnit Jupiter 5, Jackson (JSON parsing), System.Text.Json (C# side)

---

## File Structure

### New C# Reflection Tool
- Create: `tools/CollectionReflection/CollectionReflection.csproj`
- Create: `tools/CollectionReflection/Program.cs`
- Output: `tools/CollectionReflection/output/collection-api-signatures.txt`
- Output: `tools/CollectionReflection/output/collection-test-data.json`
- Output: `tools/CollectionReflection/output/collection-constants.json`

### Non-Generic Interfaces (rewrite existing)
- Rewrite: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpCollection.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IList.java` → Create: `CSharpIList.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IDictionary.java` → Create: `CSharpIDictionary.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IDictionaryEnumerator.java` → Create: `CSharpDictEnumerator.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DictionaryEntry.java` → Create: `CSharpDictEntry.java`
- Create: `CSharpIterable.java` (non-generic Iterable<Object>)
- Create: `CSharpComparer.java` (non-generic IComparer)
- Create: `CSharpEqualityComparer.java` (non-generic IEqualityComparer)
- Create: `CSharpStructuralComparable.java`
- Create: `CSharpStructuralEquatable.java`

### Non-Generic Classes
- Create: `CSharpArrayList.java`
- Create: `CSharpBitArray.java`
- Create: `CSharpHashtable.java`
- Create: `CSharpObjQueue.java` (non-generic Queue)
- Create: `CSharpObjStack.java` (non-generic Stack)
- Create: `CSharpObjSortedList.java` (non-generic SortedList)
- Create: `CSharpDefaultComparer.java` (Comparer.Default)
- Create: `CSharpCaseInsensitiveComparer.java`
- Create: `CSharpCollectionBase.java`
- Create: `CSharpDictionaryBase.java`
- Create: `CSharpReadOnlyCollectionBase.java`
- Create: `CSharpStructuralComparisons.java`

### Generic Interfaces
- Rewrite: `CSharpEnumerator.java` (support both generic and non-generic)
- Create: `CSharpIterable.java` (generic, same name different type param — Java allows this)
- Create: `CSharpICollection.java`
- Create: `CSharpIList.java` (generic)
- Create: `CSharpIDictionary.java` (generic)
- Create: `CSharpComparer.java` (generic)
- Create: `CSharpEqualityComparer.java` (generic)
- Create: `CSharpReadOnlyCollection.java`
- Create: `CSharpReadOnlyList.java`
- Create: `CSharpReadOnlyDict.java`
- Create: `CSharpReadOnlySet.java`
- Create: `CSharpISet.java`

**Design decision**: Java doesn't support reusing the same simple name for different types in the same package. Therefore:
- Non-generic interfaces get no special suffix (they use raw types: `CSharpCollection`, `CSharpIList`)
- Generic interfaces get a `Generic` suffix in the Java class name: `CSharpICollectionGeneric<T>`, `CSharpIListGeneric<T>`, `CSharpIDictionaryGeneric<K,V>`, etc.
- Or alternatively, keep non-generic with `Obj` suffix and generic without: `CSharpIListObj`, `CSharpIList<T>`. This is cleaner.

**Final naming resolution**: Since the project's TypeMappings.json maps C# types to Java class names, and the C# side has separate `System.Collections.IList` vs `System.Collections.Generic.IList<T>`, we use:
- Non-generic: `CSharpIList` (no type param, raw Object)
- Generic: `CSharpGenericIList<T>` etc.

### Generic Classes
- Rewrite: `CSharpStack.java` (enhance to implement CSharpGenericICollection<T>)
- Create: `CSharpList.java`
- Create: `CSharpDictionary.java` (with inner KeyCollection, ValueCollection)
- Create: `CSharpHashSet.java`
- Create: `CSharpSortedSet.java`
- Create: `CSharpSortedDict.java` (with inner KeyCollection, ValueCollection)
- Rewrite: `SortedList.java` → Create `CSharpSortedList.java` (generic)
- Create: `CSharpQueue.java` (generic)
- Rewrite: `LinkedListWithNodes.java` → Create `CSharpLinkedList.java`
- Rewrite: `LinkedListNode.java` → Create `CSharpLinkedListNode.java`
- Create: `CSharpPriorityQueue.java`
- Create: `CSharpKeyValuePair.java`
- Create: `CSharpDefaultComparerGeneric.java`
- Create: `CSharpDefaultEqualityComparerGeneric.java`
- Create: `CSharpRefEqualityComparer.java`
- Create: `CSharpKeyedByTypeCollection.java`
- Create: `CSharpCollectionExtensions.java`

### Tests
- Rewrite: `CSharpCollectionTest.java`
- Rewrite: `CSharpStackTest.java`
- Rewrite: `CSharpEnumeratorTest.java`
- Delete: `KeyedCollectionTest.java` → Create `CSharpKeyedByTypeCollectionTest.java`
- Delete: `LinkedListTest.java` → Create `CSharpLinkedListTest.java`
- Delete: `SortedListTest.java` → Create `CSharpSortedListTest.java`
- Create: `CSharpArrayListTest.java`
- Create: `CSharpHashtableTest.java`
- Create: `CSharpBitArrayTest.java`
- Create: `CSharpListTest.java`
- Create: `CSharpDictionaryTest.java`
- Create: `CSharpHashSetTest.java`
- Create: `CSharpSortedSetTest.java`
- Create: `CSharpSortedDictTest.java`
- Create: `CSharpQueueTest.java`
- Create: `CSharpPriorityQueueTest.java`
- Create: `CSharpKeyValuePairTest.java`

### Config
- Modify: `config/TypeMappings.json`

---

### Task 1: Create CollectionReflection C# Tool

**Files:**
- Create: `tools/CollectionReflection/CollectionReflection.csproj`
- Create: `tools/CollectionReflection/Program.cs`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the reflection program**

The program reflects on all C# collection types in System.Collections and System.Collections.Generic, extracting:
- API signatures (fields, properties, methods, constructors, indexers)
- Runtime test data (construct collections, perform operations, record input/output)
- Static constants

Output files go to `tools/CollectionReflection/output/`.

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

class Program
{
    static void Main()
    {
        Directory.CreateDirectory("output");

        // Non-generic types
        var nonGenericTypes = new Type[]
        {
            typeof(IEnumerable),
            typeof(IEnumerator),
            typeof(ICollection),
            typeof(IList),
            typeof(IDictionary),
            typeof(IDictionaryEnumerator),
            typeof(IComparer),
            typeof(IEqualityComparer),
            typeof(IStructuralComparable),
            typeof(IStructuralEquatable),
            typeof(ArrayList),
            typeof(BitArray),
            typeof(Hashtable),
            typeof(Queue),
            typeof(Stack),
            typeof(SortedList),
            typeof(DictionaryEntry),
            typeof(Comparer),
            typeof(CaseInsensitiveComparer),
            typeof(CollectionBase),
            typeof(DictionaryBase),
            typeof(ReadOnlyCollectionBase),
            typeof(StructuralComparisons),
        };

        // Generic types (use closed generic with concrete type params)
        var genericTypeDefinitions = new Type[]
        {
            typeof(List<>),
            typeof(Dictionary<,>),
            typeof(HashSet<>),
            typeof(SortedSet<>),
            typeof(SortedDictionary<,>),
            typeof(Queue<>),
            typeof(Stack<>),
            typeof(LinkedList<>),
            typeof(KeyValuePair<,>),
            typeof(Comparer<>),
            typeof(EqualityComparer<>),
            typeof(PriorityQueue<,>),
            typeof(KeyedByTypeCollection<>),
            typeof(ICollection<>),
            typeof(IList<>),
            typeof(IDictionary<,>),
            typeof(IEnumerable<>),
            typeof(IEnumerator<>),
            typeof(IComparer<>),
            typeof(IEqualityComparer<>),
            typeof(IReadOnlyCollection<>),
            typeof(IReadOnlyList<>),
            typeof(IReadOnlyDictionary<,>),
            typeof(IReadOnlySet<>),
            typeof(ISet<>),
        };

        using (var writer = new StreamWriter("output/collection-api-signatures.txt"))
        {
            foreach (var type in nonGenericTypes)
            {
                WriteTypeSignature(writer, type);
            }

            foreach (var openType in genericTypeDefinitions)
            {
                // Close with string/int for signature inspection
                Type closedType = CloseGenericType(openType);
                WriteTypeSignature(writer, closedType, openType);
            }
        }

        GenerateTestData();

        GenerateConstants();
    }

    static Type CloseGenericType(Type openType)
    {
        if (openType == typeof(PriorityQueue<,>))
            return typeof(PriorityQueue<string, int>);

        var paramCount = openType.GetGenericArguments().Length;
        return paramCount switch
        {
            1 => openType.MakeGenericType(typeof(string)),
            2 => openType.MakeGenericType(typeof(string), typeof(int)),
            _ => openType
        };
    }

    static void WriteTypeSignature(StreamWriter writer, Type type, Type? openType = null)
    {
        var displayName = openType != null ? openType.FullName! : type.FullName!;
        writer.WriteLine($"=== {displayName} ===");
        writer.WriteLine();

        // Constructors
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length).ToList();
        if (ctors.Count > 0)
        {
            writer.WriteLine("--- Constructors ---");
            foreach (var c in ctors)
            {
                var parameters = string.Join(", ", c.GetParameters()
                    .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                writer.WriteLine($"  {type.Name}({parameters})");
            }
            writer.WriteLine();
        }

        // Static fields / constants
        var staticFields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.Name).ToList();
        if (staticFields.Count > 0)
        {
            writer.WriteLine("--- Static Fields / Constants ---");
            foreach (var f in staticFields)
            {
                var val = f.IsLiteral ? f.GetRawConstantValue() : "?";
                writer.WriteLine($"  {f.FieldType.GetDisplayName()} {f.Name} = {val}");
            }
            writer.WriteLine();
        }

        // Static properties
        var staticProps = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name).ToList();
        if (staticProps.Count > 0)
        {
            writer.WriteLine("--- Static Properties ---");
            foreach (var p in staticProps)
            {
                writer.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
            }
            writer.WriteLine();
        }

        // Instance properties (including indexers)
        var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name).ToList();
        if (instanceProps.Count > 0)
        {
            writer.WriteLine("--- Instance Properties ---");
            foreach (var p in instanceProps)
            {
                var indexParams = p.GetIndexParameters();
                if (indexParams.Length > 0)
                {
                    var idxParams = string.Join(", ", indexParams.Select(ip => $"{ip.ParameterType.GetDisplayName()} {ip.Name}"));
                    writer.WriteLine($"  {p.PropertyType.GetDisplayName()} this[{idxParams}] {{ {GetAccessors(p)} }}");
                }
                else
                {
                    writer.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
                }
            }
            writer.WriteLine();
        }

        // Static methods
        var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
        if (staticMethods.Count > 0)
        {
            writer.WriteLine("--- Static Methods ---");
            foreach (var m in staticMethods)
            {
                var parameters = string.Join(", ", m.GetParameters()
                    .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                writer.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
            }
            writer.WriteLine();
        }

        // Instance methods
        var instanceMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // skip property getters/setters
            .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
        if (instanceMethods.Count > 0)
        {
            writer.WriteLine("--- Instance Methods ---");
            foreach (var m in instanceMethods)
            {
                var parameters = string.Join(", ", m.GetParameters()
                    .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                writer.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
            }
            writer.WriteLine();
        }

        // Interfaces
        var interfaces = type.GetInterfaces();
        if (interfaces.Length > 0)
        {
            writer.WriteLine("--- Implements ---");
            foreach (var i in interfaces.OrderBy(i => i.Name))
            {
                writer.WriteLine($"  {i.GetDisplayName()}");
            }
            writer.WriteLine();
        }

        writer.WriteLine();
    }

    static string GetAccessors(PropertyInfo p)
    {
        var get = p.CanRead ? "get" : "";
        var set = p.CanWrite ? "set" : "";
        return string.Join("; ", new[] { get, set }.Where(s => s != ""));
    }

    static void GenerateTestData()
    {
        var testData = new List<object>();

        // ArrayList test data
        {
            var al = new ArrayList();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "ArrayList" });
            al.Add("hello");
            steps.Add(new() { ["op"] = "Add", ["input"] = "hello", ["result"] = 0, ["count"] = al.Count });
            al.Add(42);
            steps.Add(new() { ["op"] = "Add", ["input"] = 42, ["result"] = 1, ["count"] = al.Count });
            al.Add(null);
            steps.Add(new() { ["op"] = "Add", ["input"] = null, ["result"] = 2, ["count"] = al.Count });
            steps.Add(new() { ["op"] = "Count", ["result"] = al.Count });
            steps.Add(new() { ["op"] = "Contains", ["input"] = "hello", ["result"] = al.Contains("hello") });
            steps.Add(new() { ["op"] = "Contains", ["input"] = "missing", ["result"] = al.Contains("missing") });
            steps.Add(new() { ["op"] = "IndexOf", ["input"] = 42, ["result"] = al.IndexOf(42) });
            steps.Add(new() { ["op"] = "this[]", ["input"] = 0, ["result"] = al[0] });
            al.Remove("hello");
            steps.Add(new() { ["op"] = "Remove", ["input"] = "hello", ["count"] = al.Count });
            al.RemoveAt(0);
            steps.Add(new() { ["op"] = "RemoveAt", ["input"] = 0, ["count"] = al.Count });
            steps.Add(new() { ["op"] = "Count", ["result"] = al.Count });
            testData.Add(new { type = "ArrayList", steps });
        }

        // Hashtable test data
        {
            var ht = new Hashtable();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Hashtable" });
            ht.Add("a", 1);
            steps.Add(new() { ["op"] = "Add", ["input"] = new { key = "a", value = 1 }, ["count"] = ht.Count });
            ht.Add("b", 2);
            steps.Add(new() { ["op"] = "Add", ["input"] = new { key = "b", value = 2 }, ["count"] = ht.Count });
            steps.Add(new() { ["op"] = "Count", ["result"] = ht.Count });
            steps.Add(new() { ["op"] = "ContainsKey", ["input"] = "a", ["result"] = ht.ContainsKey("a") });
            steps.Add(new() { ["op"] = "ContainsKey", ["input"] = "z", ["result"] = ht.ContainsKey("z") });
            steps.Add(new() { ["op"] = "this[]", ["input"] = "a", ["result"] = ht["a"] });
            ht.Remove("a");
            steps.Add(new() { ["op"] = "Remove", ["input"] = "a", ["count"] = ht.Count });
            steps.Add(new() { ["op"] = "ContainsKey", ["input"] = "a", ["result"] = ht.ContainsKey("a") });
            testData.Add(new { type = "Hashtable", steps });
        }

        // List<string> test data
        {
            var list = new List<string>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "List<string>" });
            list.Add("alpha");
            steps.Add(new() { ["op"] = "Add", ["input"] = "alpha", ["count"] = list.Count });
            list.Add("beta");
            steps.Add(new() { ["op"] = "Add", ["input"] = "beta", ["count"] = list.Count });
            list.Add("gamma");
            steps.Add(new() { ["op"] = "Add", ["input"] = "gamma", ["count"] = list.Count });
            steps.Add(new() { ["op"] = "Count", ["result"] = list.Count });
            steps.Add(new() { ["op"] = "Contains", ["input"] = "beta", ["result"] = list.Contains("beta") });
            steps.Add(new() { ["op"] = "IndexOf", ["input"] = "beta", ["result"] = list.IndexOf("beta") });
            steps.Add(new() { ["op"] = "this[]", ["input"] = 1, ["result"] = list[1] });
            list.Sort();
            steps.Add(new() { ["op"] = "Sort", ["result"] = string.Join(",", list) });
            list.Reverse();
            steps.Add(new() { ["op"] = "Reverse", ["result"] = string.Join(",", list) });
            list.RemoveAt(0);
            steps.Add(new() { ["op"] = "RemoveAt", ["input"] = 0, ["count"] = list.Count });
            list.Add("delta");
            steps.Add(new() { ["op"] = "Add", ["input"] = "delta", ["count"] = list.Count });
            testData.Add(new { type = "List<string>", steps });
        }

        // Dictionary<string,int> test data
        {
            var dict = new Dictionary<string, int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Dictionary<string,int>" });
            dict.Add("x", 10);
            steps.Add(new() { ["op"] = "Add", ["input"] = new { key = "x", value = 10 }, ["count"] = dict.Count });
            dict.Add("y", 20);
            steps.Add(new() { ["op"] = "Add", ["input"] = new { key = "y", value = 20 }, ["count"] = dict.Count });
            steps.Add(new() { ["op"] = "Count", ["result"] = dict.Count });
            steps.Add(new() { ["op"] = "ContainsKey", ["input"] = "x", ["result"] = dict.ContainsKey("x") });
            steps.Add(new() { ["op"] = "this[]", ["input"] = "x", ["result"] = dict["x"] });
            dict["z"] = 30;
            steps.Add(new() { ["op"] = "this[]=", ["input"] = new { key = "z", value = 30 }, ["count"] = dict.Count });
            steps.Add(new() { ["op"] = "TryGetValue", ["input"] = "y", ["result"] = true, ["value"] = dict["y"] });
            dict.Remove("x");
            steps.Add(new() { ["op"] = "Remove", ["input"] = "x", ["count"] = dict.Count });
            testData.Add(new { type = "Dictionary<string,int>", steps });
        }

        // Stack<int> test data
        {
            var stack = new Stack<int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Stack<int>" });
            stack.Push(1);
            steps.Add(new() { ["op"] = "Push", ["input"] = 1, ["count"] = stack.Count });
            stack.Push(2);
            steps.Add(new() { ["op"] = "Push", ["input"] = 2, ["count"] = stack.Count });
            stack.Push(3);
            steps.Add(new() { ["op"] = "Push", ["input"] = 3, ["count"] = stack.Count });
            steps.Add(new() { ["op"] = "Peek", ["result"] = stack.Peek() });
            steps.Add(new() { ["op"] = "Pop", ["result"] = stack.Pop(), ["count"] = stack.Count });
            steps.Add(new() { ["op"] = "Contains", ["input"] = 2, ["result"] = stack.Contains(2) });
            testData.Add(new { type = "Stack<int>", steps });
        }

        // Queue<string> test data
        {
            var queue = new Queue<string>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Queue<string>" });
            queue.Enqueue("first");
            steps.Add(new() { ["op"] = "Enqueue", ["input"] = "first", ["count"] = queue.Count });
            queue.Enqueue("second");
            steps.Add(new() { ["op"] = "Enqueue", ["input"] = "second", ["count"] = queue.Count });
            steps.Add(new() { ["op"] = "Peek", ["result"] = queue.Peek() });
            steps.Add(new() { ["op"] = "Dequeue", ["result"] = queue.Dequeue(), ["count"] = queue.Count });
            steps.Add(new() { ["op"] = "Contains", ["input"] = "second", ["result"] = queue.Contains("second") });
            testData.Add(new { type = "Queue<string>", steps });
        }

        // HashSet<string> test data
        {
            var set = new HashSet<string>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "HashSet<string>" });
            steps.Add(new() { ["op"] = "Add", ["input"] = "a", ["result"] = set.Add("a"), ["count"] = set.Count });
            steps.Add(new() { ["op"] = "Add", ["input"] = "b", ["result"] = set.Add("b"), ["count"] = set.Count });
            steps.Add(new() { ["op"] = "Add", ["input"] = "a", ["result"] = set.Add("a"), ["count"] = set.Count }); // duplicate
            steps.Add(new() { ["op"] = "Contains", ["input"] = "a", ["result"] = set.Contains("a") });
            steps.Add(new() { ["op"] = "Remove", ["input"] = "a", ["result"] = set.Remove("a"), ["count"] = set.Count });
            testData.Add(new { type = "HashSet<string>", steps });
        }

        // SortedSet<int> test data
        {
            var set = new SortedSet<int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "SortedSet<int>" });
            set.Add(5); set.Add(1); set.Add(3);
            steps.Add(new() { ["op"] = "Add5,1,3", ["result"] = string.Join(",", set), ["count"] = set.Count });
            steps.Add(new() { ["op"] = "Min", ["result"] = set.Min });
            steps.Add(new() { ["op"] = "Max", ["result"] = set.Max });
            testData.Add(new { type = "SortedSet<int>", steps });
        }

        // SortedDictionary<string,int> test data
        {
            var sd = new SortedDictionary<string, int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "SortedDictionary<string,int>" });
            sd.Add("c", 3); sd.Add("a", 1); sd.Add("b", 2);
            steps.Add(new() { ["op"] = "Add_c,a,b", ["keys"] = string.Join(",", sd.Keys), ["count"] = sd.Count });
            steps.Add(new() { ["op"] = "this[]", ["input"] = "b", ["result"] = sd["b"] });
            testData.Add(new { type = "SortedDictionary<string,int>", steps });
        }

        // SortedList<string,int> test data
        {
            var sl = new SortedList<string, int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "SortedList<string,int>" });
            sl.Add("c", 3); sl.Add("a", 1); sl.Add("b", 2);
            steps.Add(new() { ["op"] = "Add_c,a,b", ["keys"] = string.Join(",", sl.Keys), ["count"] = sl.Count });
            steps.Add(new() { ["op"] = "IndexOfKey", ["input"] = "b", ["result"] = sl.IndexOfKey("b") });
            steps.Add(new() { ["op"] = "GetValueAtIndex", ["input"] = 1, ["result"] = sl.GetValueAtIndex(1) });
            testData.Add(new { type = "SortedList<string,int>", steps });
        }

        // LinkedList<string> test data
        {
            var ll = new LinkedList<string>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "LinkedList<string>" });
            ll.AddLast("a");
            steps.Add(new() { ["op"] = "AddLast", ["input"] = "a", ["count"] = ll.Count, ["first"] = ll.First.Value, ["last"] = ll.Last.Value });
            ll.AddLast("c");
            ll.AddBefore(ll.Last, "b");
            steps.Add(new() { ["op"] = "AddBefore_last_b", ["count"] = ll.Count, ["first"] = ll.First.Value, ["last"] = ll.Last.Value });
            steps.Add(new() { ["op"] = "Contains", ["input"] = "b", ["result"] = ll.Contains("b") });
            ll.Remove("b");
            steps.Add(new() { ["op"] = "Remove_b", ["count"] = ll.Count });
            testData.Add(new { type = "LinkedList<string>", steps });
        }

        // BitArray test data
        {
            var ba = new BitArray(5);
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "BitArray", ["length"] = ba.Length });
            ba.Set(0, true); ba.Set(2, true); ba.Set(4, true);
            steps.Add(new() { ["op"] = "Set_0,2,4_true", ["length"] = ba.Length, ["get0"] = ba.Get(0), ["get1"] = ba.Get(1), ["get2"] = ba.Get(2) });
            ba.SetAll(true);
            steps.Add(new() { ["op"] = "SetAll_true", ["get3"] = ba.Get(3) });
            testData.Add(new { type = "BitArray", steps });
        }

        // PriorityQueue<string,int> test data
        {
            var pq = new PriorityQueue<string, int>();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "PriorityQueue<string,int>" });
            pq.Enqueue("low", 3);
            pq.Enqueue("high", 1);
            pq.Enqueue("mid", 2);
            steps.Add(new() { ["op"] = "Enqueue_3items", ["count"] = pq.Count });
            steps.Add(new() { ["op"] = "Peek", ["result"] = pq.Peek() });
            steps.Add(new() { ["op"] = "Dequeue", ["result"] = pq.Dequeue(), ["count"] = pq.Count });
            testData.Add(new { type = "PriorityQueue<string,int>", steps });
        }

        // KeyValuePair test data
        {
            var kvp = new KeyValuePair<string, int>("test", 42);
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "KeyValuePair<string,int>" });
            steps.Add(new() { ["op"] = "Key", ["result"] = kvp.Key });
            steps.Add(new() { ["op"] = "Value", ["result"] = kvp.Value });
            testData.Add(new { type = "KeyValuePair<string,int>", steps });
        }

        // Non-generic Stack test data
        {
            var stack = new Stack();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Stack" });
            stack.Push("a"); stack.Push("b"); stack.Push("c");
            steps.Add(new() { ["op"] = "Push_a,b,c", ["count"] = stack.Count });
            steps.Add(new() { ["op"] = "Peek", ["result"] = stack.Peek() });
            steps.Add(new() { ["op"] = "Pop", ["result"] = stack.Pop(), ["count"] = stack.Count });
            testData.Add(new { type = "Stack", steps });
        }

        // Non-generic Queue test data
        {
            var queue = new Queue();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "Queue" });
            queue.Enqueue("first"); queue.Enqueue("second");
            steps.Add(new() { ["op"] = "Enqueue_2", ["count"] = queue.Count });
            steps.Add(new() { ["op"] = "Peek", ["result"] = queue.Peek() });
            steps.Add(new() { ["op"] = "Dequeue", ["result"] = queue.Dequeue(), ["count"] = queue.Count });
            testData.Add(new { type = "Queue", steps });
        }

        // Non-generic SortedList test data
        {
            var sl = new SortedList();
            var steps = new List<Dictionary<string, object>>();
            steps.Add(new() { ["op"] = "construct", ["type"] = "SortedList" });
            sl.Add("c", 3); sl.Add("a", 1); sl.Add("b", 2);
            steps.Add(new() { ["op"] = "Add_c,a,b", ["count"] = sl.Count, ["keys"] = string.Join(",", sl.Keys.Cast<string>()) });
            steps.Add(new() { ["op"] = "ContainsKey", ["input"] = "b", ["result"] = sl.ContainsKey("b") });
            steps.Add(new() { ["op"] = "this[]", ["input"] = "a", ["result"] = sl["a"] });
            testData.Add(new { type = "SortedList", steps });
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText("output/collection-test-data.json",
            JsonSerializer.Serialize(testData, options));
    }

    static void GenerateConstants()
    {
        var constants = new Dictionary<string, object>();

        constants["Comparer.Default"] = "System.Collections.Comparer.Default";
        constants["CaseInsensitiveComparer.Default"] = "System.Collections.CaseInsensitiveComparer.Default";
        constants["StructuralComparisons.StructuralComparer"] = "System.Collections.StructuralComparisons.StructuralComparer";
        constants["StructuralComparisons.StructuralEqualityComparer"] = "System.Collections.StructuralComparisons.StructuralEqualityComparer";

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText("output/collection-constants.json",
            JsonSerializer.Serialize(constants, options));
    }
}

static class TypeExtensions
{
    public static string GetDisplayName(this Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericArgs = type.GetGenericArguments();
            var name = genericDef.Name.Substring(0, genericDef.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", genericArgs.Select(GetDisplayName))}>";
        }
        if (type.IsArray)
        {
            return $"{GetDisplayName(type.GetElementType())}[]";
        }
        if (type.IsNested)
        {
            return $"{type.DeclaringType.Name}.{type.Name}";
        }
        if (type.IsByRef)
        {
            return $"ref {GetDisplayName(type.GetElementType())}";
        }

        var map = new Dictionary<Type, string>
        {
            { typeof(void), "void" },
            { typeof(bool), "bool" },
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(decimal), "decimal" },
            { typeof(string), "string" },
            { typeof(object), "object" },
            { typeof(char), "char" },
            { typeof(byte), "byte" },
        };
        return map.TryGetValue(type, out var alias) ? alias : type.Name;
    }
}
```

- [ ] **Step 3: Run the reflection tool**

Run: `cd tools/CollectionReflection && dotnet run`
Expected: Three output files generated in `tools/CollectionReflection/output/`

- [ ] **Step 4: Verify output files exist**

Run: `ls tools/CollectionReflection/output/`
Expected: `collection-api-signatures.txt`, `collection-test-data.json`, `collection-constants.json`

- [ ] **Step 5: Commit**

```bash
git add tools/CollectionReflection/
git commit -m "Add CollectionReflection tool for C# collection API extraction"
```

---

### Task 2: Rewrite Non-Generic Interfaces

**Files:**
- Rewrite: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpCollection.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpIterable.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IList.java` → Create: `CSharpIList.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IDictionary.java` → Create: `CSharpIDictionary.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/IDictionaryEnumerator.java` → Create: `CSharpDictEnumerator.java`
- Delete: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/DictionaryEntry.java` → Create: `CSharpDictEntry.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpComparer.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpEqualityComparer.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStructuralComparable.java`
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStructuralEquatable.java`

- [ ] **Step 1: Write CSharpIterable interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IEnumerable
public interface CSharpIterable extends Iterable<Object> {
    CSharpEnumerator iterator();
}
```

- [ ] **Step 2: Rewrite CSharpCollection interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.ICollection
public interface CSharpCollection extends CSharpIterable {
    default int size() { return getCount(); }
    default int getCount() { return size(); }

    void copyTo(Object[] array, int index);

    boolean getIsSynchronized();
    Object getSyncRoot();
}
```

- [ ] **Step 3: Create CSharpIList interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IList
public interface CSharpIList extends CSharpCollection {
    int add(Object value);
    void clear();
    boolean contains(Object value);
    int indexOf(Object value);
    void insert(int index, Object value);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    void remove(Object value);
    void removeAt(int index);
    Object get(int index);
    Object set(int index, Object value);
}
```

- [ ] **Step 4: Create CSharpIDictionary interface**

```java
package io.github.ningpp.compat;

import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

// Maps to System.Collections.IDictionary
public interface CSharpIDictionary extends CSharpCollection {
    void add(Object key, Object value);
    void clear();
    boolean contains(Object key);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    CSharpCollection getKeys();
    CSharpCollection getValues();
    void remove(Object key);
    Object get(Object key);
    Object put(Object key, Object value);

    default Set<Map.Entry<Object, Object>> entrySet() {
        LinkedHashSet<Map.Entry<Object, Object>> entries = new LinkedHashSet<>();
        for (Object item : this) {
            if (item instanceof Map.Entry<?, ?> entry) {
                entries.add(new CSharpDictEntry(entry.getKey(), entry.getValue()));
            }
        }
        return entries;
    }
}
```

- [ ] **Step 5: Create CSharpDictEntry**

```java
package io.github.ningpp.compat;

import java.util.Map;

// Maps to System.Collections.DictionaryEntry
public final class CSharpDictEntry implements Map.Entry<Object, Object> {
    private final Object key;
    private Object value;

    public CSharpDictEntry(Object key, Object value) {
        this.key = key;
        this.value = value;
    }

    @Override public Object getKey() { return key; }
    @Override public Object getValue() { return value; }
    @Override public Object setValue(Object value) {
        Object previous = this.value;
        this.value = value;
        return previous;
    }
}
```

- [ ] **Step 6: Create CSharpDictEnumerator**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IDictionaryEnumerator
public interface CSharpDictEnumerator extends CSharpEnumerator {
    CSharpDictEntry getEntry();
    Object getKey();
    Object getValue();

    @Override
    default Object getCurrent() { return getEntry(); }

    @Override
    default boolean hasNext() { return moveNext(); }

    @Override
    default Object next() { return getCurrent(); }
}
```

Note: `CSharpEnumerator` is rewritten in this same task to be the non-generic base (see Step 7).

- [ ] **Step 7: Rewrite CSharpEnumerator (non-generic base)**

The existing `CSharpEnumerator<T>` will be restructured. The non-generic `CSharpEnumerator` becomes the base, and the generic version becomes `CSharpEnumerator<T>`.

```java
package io.github.ningpp.compat;

import java.util.Iterator;
import java.util.NoSuchElementException;

// Maps to System.Collections.IEnumerator (non-generic base)
public interface CSharpEnumerator extends Iterator<Object> {
    static CSharpEnumerator from(Iterator<Object> iterator) {
        return new IteratorBackedCSharpEnumerator(iterator);
    }

    boolean moveNext();
    Object getCurrent();

    default void reset() {
        throw new UnsupportedOperationException("Reset is not supported");
    }

    final class IteratorBackedCSharpEnumerator implements CSharpEnumerator {
        private final Iterator<Object> iterator;
        private Object current;
        private boolean hasCurrent;

        IteratorBackedCSharpEnumerator(Iterator<Object> iterator) {
            this.iterator = iterator;
        }

        @Override public boolean moveNext() {
            if (!iterator.hasNext()) { current = null; hasCurrent = false; return false; }
            current = iterator.next();
            hasCurrent = true;
            return true;
        }

        @Override public Object getCurrent() {
            if (!hasCurrent) throw new NoSuchElementException();
            return current;
        }

        @Override public boolean hasNext() { return iterator.hasNext(); }
        @Override public Object next() { return iterator.next(); }
    }
}
```

- [ ] **Step 8: Create CSharpComparer interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IComparer
public interface CSharpComparer {
    int compare(Object a, Object b);
}
```

- [ ] **Step 9: Create CSharpEqualityComparer interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IEqualityComparer
public interface CSharpEqualityComparer {
    boolean equals(Object x, Object y);
    int hashCode(Object obj);
}
```

- [ ] **Step 10: Create CSharpStructuralComparable interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IStructuralComparable
public interface CSharpStructuralComparable {
    int compareTo(Object other, CSharpComparer comparer);
}
```

- [ ] **Step 11: Create CSharpStructuralEquatable interface**

```java
package io.github.ningpp.compat;

// Maps to System.Collections.IStructuralEquatable
public interface CSharpStructuralEquatable {
    boolean equals(Object other, CSharpEqualityComparer comparer);
    int hashCode(CSharpEqualityComparer comparer);
}
```

- [ ] **Step 12: Delete old files**

Delete: `IList.java`, `IDictionary.java`, `IDictionaryEnumerator.java`, `DictionaryEntry.java`

- [ ] **Step 13: Run `mvn compile` to verify compilation**

Run: `cd java/csharptojava-compat && mvn compile`
Expected: SUCCESS (note: other files referencing old names will need updating — fix them)

- [ ] **Step 14: Fix any compilation errors from renaming**

Search for references to `IList`, `IDictionary`, `IDictionaryEnumerator`, `DictionaryEntry` in the codebase and update to new names. Add re-export type aliases if needed for backward compatibility.

- [ ] **Step 15: Run `mvn test` to verify all existing tests still pass**

Run: `cd java/csharptojava-compat && mvn test`
Expected: All tests pass

- [ ] **Step 16: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Rewrite non-generic collection interfaces (IEnumerable/ICollection/IList/IDictionary)"
```

---

### Task 3: Implement Non-Generic Collection Classes

**Files:**
- Create: `CSharpArrayList.java`
- Create: `CSharpBitArray.java`
- Create: `CSharpHashtable.java`
- Create: `CSharpObjQueue.java`
- Create: `CSharpObjStack.java`
- Create: `CSharpObjSortedList.java`
- Create: `CSharpDefaultComparer.java`
- Create: `CSharpCaseInsensitiveComparer.java`
- Create: `CSharpStructuralComparisons.java`

- [ ] **Step 1: Implement CSharpArrayList**

Wraps `ArrayList<Object>`, implements `CSharpIList`. Key methods: `add`, `clear`, `contains`, `indexOf`, `insert`, `remove`, `removeAt`, `get`, `set`, `copyTo`, `getCount`, `getIsFixedSize`, `getIsReadOnly`, `getIsSynchronized`, `getSyncRoot`, `addRange`, `binarySearch`, `getRange`, `toArray`, `trimToSize`, `repeat` (static), `reverse`, `sort`, `clone`.

- [ ] **Step 2: Implement CSharpBitArray**

Wraps `BitSet`, adds `Length` tracking. Key methods: `get`, `set`, `setAll`, `and`, `or`, `not`, `xor`, `leftShift`, `rightShift`, `clone`, `copyTo`, `length`, `count`.

- [ ] **Step 3: Implement CSharpHashtable**

Wraps `LinkedHashMap<Object,Object>`, implements `CSharpIDictionary`. Key methods: `add`, `clear`, `contains`, `containsKey`, `containsValue`, `remove`, `get`, `put`, `getKeys`, `getValues`, `getCount`, `clone`, `copyTo`.

- [ ] **Step 4: Implement CSharpObjQueue**

Wraps `LinkedList<Object>`. Key methods: `enqueue`, `dequeue`, `peek`, `clear`, `contains`, `copyTo`, `toArray`, `clone`.

- [ ] **Step 5: Implement CSharpObjStack**

Wraps `ArrayDeque<Object>`. Key methods: `push`, `pop`, `peek`, `clear`, `contains`, `copyTo`, `toArray`, `clone`.

- [ ] **Step 6: Implement CSharpObjSortedList**

Wraps `TreeMap<Object,Object>`. Key methods: `add`, `clear`, `containsKey`, `containsValue`, `remove`, `get`, `setByIndex`, `getByIndex`, `getKey`, `getValueList`, `getKeys`, `getValues`, `indexOfKey`, `indexOfValue`, `clone`, `copyTo`, `getCount`.

- [ ] **Step 7: Implement CSharpDefaultComparer**

Singleton implementing `CSharpComparer`. Delegates to `Comparator.naturalOrder()` for Comparables, throws for non-Comparable.

- [ ] **Step 8: Implement CSharpCaseInsensitiveComparer**

Implements `CSharpComparer`. Delegates to `String.CASE_INSENSITIVE_ORDER` for Strings, falls back to natural order.

- [ ] **Step 9: Implement CSharpStructuralComparisons**

Static class with `getStructuralComparer()` and `getStructuralEqualityComparer()` factory methods.

- [ ] **Step 10: Run `mvn compile` to verify**

Run: `cd java/csharptojava-compat && mvn compile`
Expected: SUCCESS

- [ ] **Step 11: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Implement non-generic collection classes (ArrayList/Hashtable/Queue/Stack/BitArray/SortedList)"
```

---

### Task 4: Implement Generic Interfaces

**Files:**
- Create: `CSharpGenericIterable.java` (generic Iterable<T>)
- Create: `CSharpGenericEnumerator.java` (generic IEnumerator<T>)
- Rewrite: `CSharpEnumerator.java` (already done in Task 2 as non-generic)
- Create: `CSharpICollection.java` (generic)
- Create: `CSharpGenericIList.java`
- Create: `CSharpGenericIDictionary.java`
- Create: `CSharpGenericComparer.java`
- Create: `CSharpGenericEqualityComparer.java`
- Create: `CSharpReadOnlyCollection.java`
- Create: `CSharpReadOnlyList.java`
- Create: `CSharpReadOnlyDict.java`
- Create: `CSharpReadOnlySet.java`
- Create: `CSharpISet.java`

Note on naming: Since Java doesn't allow same-name classes with different type params in one package, generic interfaces use `Generic` prefix in class name but are mapped in TypeMappings.json without the prefix (the mapping handles the disambiguation).

- [ ] **Step 1: Implement all generic interfaces**

Each interface mirrors its C# counterpart with Java-idiomatic methods:
- `CSharpGenericIterable<T>` extends `Iterable<T>`
- `CSharpGenericEnumerator<T>` extends `Iterator<T>`, has `moveNext()`, `getCurrent()`, `reset()`
- `CSharpICollection<T>` extends `CSharpGenericIterable<T>`, has `add`, `clear`, `contains`, `copyTo`, `remove`, `getCount`, `getIsReadOnly`
- `CSharpGenericIList<T>` extends `CSharpICollection<T>`, has `indexOf`, `insert`, `removeAt`, `get`, `set`
- `CSharpGenericIDictionary<K,V>` extends `CSharpICollection<CSharpKeyValuePair<K,V>>`, has `add`, `containsKey`, `remove`, `tryGetValue`, `getKeys`, `getValues`, `get`, `put`
- `CSharpGenericComparer<T>` functional interface: `compare(T, T)`
- `CSharpGenericEqualityComparer<T>` functional interface: `equals(T, T)`, `hashCode(T)`
- `CSharpReadOnlyCollection<T>` extends `CSharpGenericIterable<T>`, has `getCount`
- `CSharpReadOnlyList<T>` extends `CSharpReadOnlyCollection<T>`, has `get`
- `CSharpReadOnlyDict<K,V>` extends `CSharpReadOnlyCollection<CSharpKeyValuePair<K,V>>`, has `containsKey`, `tryGetValue`, `getKeys`, `getValues`, `get`
- `CSharpReadOnlySet<T>` extends `CSharpReadOnlyCollection<T>`, has `contains`, `isProperSubsetOf`, etc.
- `CSharpISet<T>` extends `CSharpICollection<T>`, has `add`, `exceptWith`, `intersectWith`, etc.

- [ ] **Step 2: Run `mvn compile` to verify**

- [ ] **Step 3: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Implement generic collection interfaces (ICollection<T>/IList<T>/IDictionary<K,V>/ISet<T>/IReadOnly*)"
```

---

### Task 5: Implement Core Generic Collection Classes (Part 1 — List, Dictionary, HashSet, SortedSet)

**Files:**
- Create: `CSharpList.java`
- Create: `CSharpDictionary.java` (with inner KeyCollection, ValueCollection)
- Create: `CSharpHashSet.java`
- Create: `CSharpSortedSet.java`
- Create: `CSharpKeyValuePair.java`

- [ ] **Step 1: Implement CSharpList<T>**

Wraps `ArrayList<T>`, implements `CSharpGenericIList<T>`. Full API:
- Constructors: `()`, `(int capacity)`, `(Collection<? extends T> c)`
- Add/AddRange/AsReadOnly/BinarySearch/Clear/Contains/CopyTo/EnsureCapacity
- Exists/Find/FindAll/FindIndex/FindLast/FindLastIndex/ForEach
- GetRange/IndexOf/Insert/InsertRange/LastIndexOf/Remove/RemoveAll/RemoveAt/RemoveRange
- Reverse/Sort/ToArray/TrimExcess/TrueForAll
- Properties: Count, Capacity, IsReadOnly
- Indexer: get(int), set(int, T)

- [ ] **Step 2: Implement CSharpDictionary<K,V>**

Wraps `LinkedHashMap<K,V>`, implements `CSharpGenericIDictionary<K,V>`. Inner classes:
- `KeyCollection` — wraps dictionary's keySet, implements `CSharpICollection<K>`
- `ValueCollection` — wraps dictionary's values, implements `CSharpICollection<V>`
- Full API: Add/Clear/ContainsKey/ContainsValue/EnsureCapacity/Remove/TrimExcess/TryAdd/TryGetValue
- Properties: Count, Keys, Values, Comparer
- Indexer: get(K), put(K, V)

- [ ] **Step 3: Implement CSharpHashSet<T>**

Wraps `LinkedHashSet<T>`, implements `CSharpISet<T>`. Full API:
- Add/Clear/Contains/CopyTo/EnsureCapacity/ExceptWith/IntersectWith
- IsProperSubsetOf/IsProperSupersetOf/IsSubsetOf/IsSupersetOf/Overlaps
- Remove/SetEquals/SymmetricExceptWith/TrimExcess/UnionWith
- Properties: Count, Comparer

- [ ] **Step 4: Implement CSharpSortedSet<T>**

Wraps `TreeSet<T>`, implements `CSharpISet<T>`. Additional methods:
- GetViewBetween/Reverse/Min/Max

- [ ] **Step 5: Implement CSharpKeyValuePair<K,V>**

Value object with `getKey()`, `getValue()`, `toString()`, `deconstruct()`.

- [ ] **Step 6: Run `mvn compile`**

- [ ] **Step 7: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Implement core generic collections (List/Dictionary/HashSet/SortedSet/KeyValuePair)"
```

---

### Task 6: Implement Core Generic Collection Classes (Part 2 — SortedDict, SortedList, Queue, Stack, LinkedList, PriorityQueue)

**Files:**
- Create: `CSharpSortedDict.java` (with inner KeyCollection, ValueCollection)
- Create: `CSharpSortedList.java` (rewrite of existing SortedList)
- Create: `CSharpQueue.java` (generic)
- Rewrite: `CSharpStack.java` (enhance existing)
- Create: `CSharpLinkedList.java` (rewrite of LinkedListWithNodes)
- Create: `CSharpLinkedListNode.java` (rewrite of LinkedListNode)
- Create: `CSharpPriorityQueue.java`

- [ ] **Step 1: Implement CSharpSortedDict<K,V>**

Wraps `TreeMap<K,V>`, implements `CSharpGenericIDictionary<K,V>`. Inner classes for KeyCollection/ValueCollection.

- [ ] **Step 2: Implement CSharpSortedList<K,V>**

Rewrite of existing `SortedList.java`. Wraps `TreeMap<K,V>` with index-based access. Adds `Add/Clear/ContainsKey/ContainsValue/EnsureCapacity/GetValueOrDefault/IndexOfKey/IndexOfValue/Remove/RemoveAt/TrimExcess/TryGetValue/GetKeyAtIndex/GetValueAtIndex`.

- [ ] **Step 3: Implement CSharpQueue<T>**

Wraps `java.util.LinkedList<T>`. Full API: Enqueue/Dequeue/Peek/Clear/Contains/CopyTo/EnsureCapacity/GetEnumerator/ToArray/TrimExcess/TryDequeue/TryPeek.

- [ ] **Step 4: Rewrite CSharpStack<T>**

Enhance existing to implement `CSharpICollection<T>`. Add missing methods: `EnsureCapacity`, `TrimExcess`, `TryPeek`, `TryPop`, `GetEnumerator`, `CopyTo`, `ToArray`.

- [ ] **Step 5: Implement CSharpLinkedList<T>**

Rewrite `LinkedListWithNodes<T>` as `CSharpLinkedList<T>`. Hand-written doubly-linked list with full node access. API: AddAfter/AddBefore/AddFirst/AddLast/Clear/Contains/CopyTo/Find/FindLast/Remove/RemoveFirst/RemoveLast. Properties: Count, First, Last.

- [ ] **Step 6: Implement CSharpLinkedListNode<T>**

Rewrite `LinkedListNode<T>` as `CSharpLinkedListNode<T>`. Properties: List, Next, Previous, Value, ValueRef (getter only in Java).

- [ ] **Step 7: Implement CSharpPriorityQueue<E,P>**

Wraps `java.util.PriorityQueue` with priority tracking. API: Enqueue/Dequeue/Peek/Clear/EnsureCapacity/TrimExcess/TryDequeue/TryPeek. Properties: Count, UnorderedItems.

- [ ] **Step 8: Delete old files**

Delete: `SortedList.java`, `LinkedListWithNodes.java`, `LinkedListNode.java`

- [ ] **Step 9: Run `mvn compile`**

- [ ] **Step 10: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Implement generic collections part 2 (SortedDict/SortedList/Queue/Stack/LinkedList/PriorityQueue)"
```

---

### Task 7: Implement Remaining Generic Classes

**Files:**
- Create: `CSharpDefaultComparerGeneric.java`
- Create: `CSharpDefaultEqualityComparerGeneric.java`
- Create: `CSharpRefEqualityComparer.java`
- Create: `CSharpKeyedByTypeCollection.java`
- Create: `CSharpCollectionExtensions.java`
- Create: `CSharpCollectionBase.java` (non-generic abstract)
- Create: `CSharpDictionaryBase.java` (non-generic abstract)
- Create: `CSharpReadOnlyCollectionBase.java` (non-generic abstract)

- [ ] **Step 1: Implement CSharpDefaultComparerGeneric<T>**

Singleton pattern with `defaultInstance()`. Delegates to `Comparator.naturalOrder()` for Comparable types.

- [ ] **Step 2: Implement CSharpDefaultEqualityComparerGeneric<T>**

Singleton pattern with `defaultInstance()`. Uses `Object.equals()` and `Object.hashCode()`.

- [ ] **Step 3: Implement CSharpRefEqualityComparer**

Implements `CSharpGenericEqualityComparer<Object>`. Uses `IdentityHashMap` semantics — `==` for equals, `System.identityHashCode()` for hashCode.

- [ ] **Step 4: Implement CSharpKeyedByTypeCollection<T>**

Extends `CSharpList<T>`. Additional methods: `containsType()`, `findByType()`, `findAllByType()`, `removeByType()`.

- [ ] **Step 5: Implement CSharpCollectionExtensions**

Static utility class with methods: `addRange`, `insertRange`, `getOrAdd`, `asReadOnly`.

- [ ] **Step 6: Implement CSharpCollectionBase, CSharpDictionaryBase, CSharpReadOnlyCollectionBase**

Abstract base classes with template method pattern for OnClear/OnInsert/OnRemove/OnSet/OnValidate hooks.

- [ ] **Step 7: Run `mvn compile`**

- [ ] **Step 8: Commit**

```bash
git add -A java/csharptojava-compat/src/main/java/io/github/ningpp/compat/
git commit -m "Implement remaining collection classes (Comparer/EqualityComparer/KeyedByTypeCollection/base classes)"
```

---

### Task 8: Write Tests Using C# Reflection Oracle

**Files:**
- Create/rewrite all test files listed in the File Structure section
- Copy `tools/CollectionReflection/output/collection-test-data.json` to test resources

- [ ] **Step 1: Copy test data to Java test resources**

```bash
mkdir -p java/csharptojava-compat/src/test/resources
cp tools/CollectionReflection/output/collection-test-data.json java/csharptojava-compat/src/test/resources/
```

- [ ] **Step 2: Write CSharpArrayListTest**

Test against oracle data from `collection-test-data.json`. Verify: construction, Add, Count, Contains, IndexOf, this[], Remove, RemoveAt, AddRange, BinarySearch, CopyTo, Sort, Reverse, ToArray, TrimToSize, Clone, GetRange.

- [ ] **Step 3: Write CSharpHashtableTest**

Verify: Add, Count, ContainsKey, ContainsValue, this[], Remove, Keys, Values, Clone, CopyTo.

- [ ] **Step 4: Write CSharpBitArrayTest**

Verify: constructors, Get/Set/SetAll, And/Or/Not/Xor, LeftShift/RightShift, Length, Count, Clone, CopyTo.

- [ ] **Step 5: Write CSharpListTest**

Verify against oracle: Add, Count, Contains, IndexOf, this[], Sort, Reverse, RemoveAt, AddRange, BinarySearch, CopyTo, Find, FindAll, FindIndex, ForEach, GetRange, Insert, InsertRange, LastIndexOf, Remove, RemoveAll, RemoveRange, ToArray, TrimExcess, TrueForAll, EnsureCapacity, AsReadOnly.

- [ ] **Step 6: Write CSharpDictionaryTest**

Verify against oracle: Add, Count, ContainsKey, this[], TryGetValue, Remove, Keys, Values, TryAdd, EnsureCapacity, TrimExcess, Clear.

- [ ] **Step 7: Write CSharpHashSetTest**

Verify against oracle: Add (returns boolean), Contains, Remove, Count, ExceptWith, IntersectWith, UnionWith, SymmetricExceptWith, IsSubsetOf, IsSupersetOf, SetEquals, Overlaps, CopyTo, TrimExcess, EnsureCapacity.

- [ ] **Step 8: Write CSharpSortedSetTest**

Verify against oracle: Add, Min, Max, GetViewBetween, Reverse, plus all ISet operations.

- [ ] **Step 9: Write CSharpStackTest** (rewrite existing)

Enhance existing tests, add oracle-based verification.

- [ ] **Step 10: Write CSharpQueueTest**

Verify against oracle: Enqueue, Dequeue, Peek, Count, Contains, Clear, CopyTo, ToArray, TrimExcess, EnsureCapacity, TryDequeue, TryPeek.

- [ ] **Step 11: Write CSharpLinkedListTest** (rewrite existing)

Verify: AddFirst, AddLast, AddAfter, AddBefore, Remove, RemoveFirst, RemoveLast, Contains, Find, FindLast, Clear, Count, First, Last, node navigation (Next, Previous, Value, List).

- [ ] **Step 12: Write CSharpPriorityQueueTest**

Verify against oracle: Enqueue, Dequeue, Peek, Count, Clear, TryDequeue, TryPeek, EnsureCapacity, TrimExcess.

- [ ] **Step 13: Write remaining tests** (CSharpSortedDictTest, CSharpSortedListTest, CSharpKeyValuePairTest, CSharpObjQueueTest, CSharpObjStackTest, CSharpObjSortedListTest)

- [ ] **Step 14: Run `mvn test`**

Run: `cd java/csharptojava-compat && mvn test`
Expected: All tests pass

- [ ] **Step 15: Commit**

```bash
git add -A java/csharptojava-compat/
git commit -m "Add comprehensive tests for all collection compat classes with C# oracle verification"
```

---

### Task 9: Update TypeMappings.json

**Files:**
- Modify: `config/TypeMappings.json`

- [ ] **Step 1: Update non-generic type mappings**

Replace existing mappings that pointed to old names (IList → CSharpIList, IDictionary → CSharpIDictionary, etc.) and add new mappings:

```json
{
    "csharp": "System.Collections.IEnumerable",
    "java": "CSharpIterable",
    "imports": ["io.github.ningpp.compat.CSharpIterable"]
},
{
    "csharp": "System.Collections.IEnumerator",
    "java": "CSharpEnumerator",
    "imports": ["io.github.ningpp.compat.CSharpEnumerator"]
},
{
    "csharp": "System.Collections.ICollection",
    "java": "CSharpCollection",
    "imports": ["io.github.ningpp.compat.CSharpCollection"]
},
{
    "csharp": "System.Collections.IList",
    "java": "CSharpIList",
    "imports": ["io.github.ningpp.compat.CSharpIList"]
},
{
    "csharp": "System.Collections.IDictionary",
    "java": "CSharpIDictionary",
    "imports": ["io.github.ningpp.compat.CSharpIDictionary"]
},
{
    "csharp": "System.Collections.IDictionaryEnumerator",
    "java": "CSharpDictEnumerator",
    "imports": ["io.github.ningpp.compat.CSharpDictEnumerator"]
},
{
    "csharp": "System.Collections.DictionaryEntry",
    "java": "CSharpDictEntry",
    "imports": ["io.github.ningpp.compat.CSharpDictEntry"]
},
{
    "csharp": "System.Collections.IComparer",
    "java": "CSharpComparer",
    "imports": ["io.github.ningpp.compat.CSharpComparer"]
},
{
    "csharp": "System.Collections.IEqualityComparer",
    "java": "CSharpEqualityComparer",
    "imports": ["io.github.ningpp.compat.CSharpEqualityComparer"]
},
{
    "csharp": "System.Collections.IStructuralComparable",
    "java": "CSharpStructuralComparable",
    "imports": ["io.github.ningpp.compat.CSharpStructuralComparable"]
},
{
    "csharp": "System.Collections.IStructuralEquatable",
    "java": "CSharpStructuralEquatable",
    "imports": ["io.github.ningpp.compat.CSharpStructuralEquatable"]
},
{
    "csharp": "System.Collections.ArrayList",
    "java": "CSharpArrayList",
    "imports": ["io.github.ningpp.compat.CSharpArrayList"]
},
{
    "csharp": "System.Collections.BitArray",
    "java": "CSharpBitArray",
    "imports": ["io.github.ningpp.compat.CSharpBitArray"]
},
{
    "csharp": "System.Collections.Hashtable",
    "java": "CSharpHashtable",
    "imports": ["io.github.ningpp.compat.CSharpHashtable"]
},
{
    "csharp": "System.Collections.Queue",
    "java": "CSharpObjQueue",
    "imports": ["io.github.ningpp.compat.CSharpObjQueue"]
},
{
    "csharp": "System.Collections.Stack",
    "java": "CSharpObjStack",
    "imports": ["io.github.ningpp.compat.CSharpObjStack"]
},
{
    "csharp": "System.Collections.SortedList",
    "java": "CSharpObjSortedList",
    "imports": ["io.github.ningpp.compat.CSharpObjSortedList"]
},
{
    "csharp": "System.Collections.Comparer",
    "java": "CSharpDefaultComparer",
    "imports": ["io.github.ningpp.compat.CSharpDefaultComparer"]
},
{
    "csharp": "System.Collections.CaseInsensitiveComparer",
    "java": "CSharpCaseInsensitiveComparer",
    "imports": ["io.github.ningpp.compat.CSharpCaseInsensitiveComparer"]
},
{
    "csharp": "System.Collections.CollectionBase",
    "java": "CSharpCollectionBase",
    "imports": ["io.github.ningpp.compat.CSharpCollectionBase"]
},
{
    "csharp": "System.Collections.DictionaryBase",
    "java": "CSharpDictionaryBase",
    "imports": ["io.github.ningpp.compat.CSharpDictionaryBase"]
},
{
    "csharp": "System.Collections.ReadOnlyCollectionBase",
    "java": "CSharpReadOnlyCollectionBase",
    "imports": ["io.github.ningpp.compat.CSharpReadOnlyCollectionBase"]
},
{
    "csharp": "System.Collections.StructuralComparisons",
    "java": "CSharpStructuralComparisons",
    "imports": ["io.github.ningpp.compat.CSharpStructuralComparisons"]
}
```

- [ ] **Step 2: Update generic type mappings**

Replace existing generic mappings to use new class names:

```json
{
    "csharp": "System.Collections.Generic.List`1",
    "java": "CSharpList",
    "imports": ["io.github.ningpp.compat.CSharpList"]
},
{
    "csharp": "System.Collections.Generic.Dictionary`2",
    "java": "CSharpDictionary",
    "imports": ["io.github.ningpp.compat.CSharpDictionary"]
},
{
    "csharp": "System.Collections.Generic.HashSet`1",
    "java": "CSharpHashSet",
    "imports": ["io.github.ningpp.compat.CSharpHashSet"]
},
{
    "csharp": "System.Collections.Generic.SortedSet`1",
    "java": "CSharpSortedSet",
    "imports": ["io.github.ningpp.compat.CSharpSortedSet"]
},
{
    "csharp": "System.Collections.Generic.SortedDictionary`2",
    "java": "CSharpSortedDict",
    "imports": ["io.github.ningpp.compat.CSharpSortedDict"]
},
{
    "csharp": "System.Collections.Generic.SortedList`2",
    "java": "CSharpSortedList",
    "imports": ["io.github.ningpp.compat.CSharpSortedList"]
},
{
    "csharp": "System.Collections.Generic.Queue`1",
    "java": "CSharpQueue",
    "imports": ["io.github.ningpp.compat.CSharpQueue"]
},
{
    "csharp": "System.Collections.Generic.Stack`1",
    "java": "CSharpStack",
    "imports": ["io.github.ningpp.compat.CSharpStack"]
},
{
    "csharp": "System.Collections.Generic.LinkedList`1",
    "java": "CSharpLinkedList",
    "imports": ["io.github.ningpp.compat.CSharpLinkedList"]
},
{
    "csharp": "System.Collections.Generic.LinkedListNode`1",
    "java": "CSharpLinkedListNode",
    "imports": ["io.github.ningpp.compat.CSharpLinkedListNode"]
},
{
    "csharp": "System.Collections.Generic.PriorityQueue`2",
    "java": "CSharpPriorityQueue",
    "imports": ["io.github.ningpp.compat.CSharpPriorityQueue"]
},
{
    "csharp": "System.Collections.Generic.KeyValuePair`2",
    "java": "CSharpKeyValuePair",
    "imports": ["io.github.ningpp.compat.CSharpKeyValuePair"]
},
{
    "csharp": "System.Collections.Generic.Comparer`1",
    "java": "CSharpDefaultComparerGeneric",
    "imports": ["io.github.ningpp.compat.CSharpDefaultComparerGeneric"]
},
{
    "csharp": "System.Collections.Generic.EqualityComparer`1",
    "java": "CSharpDefaultEqualityComparerGeneric",
    "imports": ["io.github.ningpp.compat.CSharpDefaultEqualityComparerGeneric"]
},
{
    "csharp": "System.Collections.Generic.ReferenceEqualityComparer",
    "java": "CSharpRefEqualityComparer",
    "imports": ["io.github.ningpp.compat.CSharpRefEqualityComparer"]
},
{
    "csharp": "System.Collections.Generic.KeyedByTypeCollection`1",
    "java": "CSharpKeyedByTypeCollection",
    "imports": ["io.github.ningpp.compat.CSharpKeyedByTypeCollection"]
},
{
    "csharp": "System.Collections.Generic.ICollection`1",
    "java": "CSharpICollection",
    "imports": ["io.github.ningpp.compat.CSharpICollection"]
},
{
    "csharp": "System.Collections.Generic.IList`1",
    "java": "CSharpGenericIList",
    "imports": ["io.github.ningpp.compat.CSharpGenericIList"]
},
{
    "csharp": "System.Collections.Generic.IDictionary`2",
    "java": "CSharpGenericIDictionary",
    "imports": ["io.github.ningpp.compat.CSharpGenericIDictionary"]
},
{
    "csharp": "System.Collections.Generic.IEnumerable`1",
    "java": "CSharpGenericIterable",
    "imports": ["io.github.ningpp.compat.CSharpGenericIterable"]
},
{
    "csharp": "System.Collections.Generic.IEnumerator`1",
    "java": "CSharpGenericEnumerator",
    "imports": ["io.github.ningpp.compat.CSharpGenericEnumerator"]
},
{
    "csharp": "System.Collections.Generic.IComparer`1",
    "java": "CSharpGenericComparer",
    "imports": ["io.github.ningpp.compat.CSharpGenericComparer"]
},
{
    "csharp": "System.Collections.Generic.IEqualityComparer`1",
    "java": "CSharpGenericEqualityComparer",
    "imports": ["io.github.ningpp.compat.CSharpGenericEqualityComparer"]
},
{
    "csharp": "System.Collections.Generic.IReadOnlyCollection`1",
    "java": "CSharpReadOnlyCollection",
    "imports": ["io.github.ningpp.compat.CSharpReadOnlyCollection"]
},
{
    "csharp": "System.Collections.Generic.IReadOnlyList`1",
    "java": "CSharpReadOnlyList",
    "imports": ["io.github.ningpp.compat.CSharpReadOnlyList"]
},
{
    "csharp": "System.Collections.Generic.IReadOnlyDictionary`2",
    "java": "CSharpReadOnlyDict",
    "imports": ["io.github.ningpp.compat.CSharpReadOnlyDict"]
},
{
    "csharp": "System.Collections.Generic.IReadOnlySet`1",
    "java": "CSharpReadOnlySet",
    "imports": ["io.github.ningpp.compat.CSharpReadOnlySet"]
},
{
    "csharp": "System.Collections.Generic.ISet`1",
    "java": "CSharpISet",
    "imports": ["io.github.ningpp.compat.CSharpISet"]
}
```

- [ ] **Step 3: Add method mappings for collection types**

Add entries to `methodMappings` section for C# method name → Java method name conversions. Key additions:

```json
{"type": "System.Collections.ArrayList", "methods": [
    {"csharp": "Add", "java": "add"},
    {"csharp": "AddRange", "java": "addRange"},
    {"csharp": "BinarySearch", "java": "binarySearch"},
    {"csharp": "Capacity", "java": "capacity"},
    {"csharp": "Clone", "java": "clone"},
    {"csharp": "FixedSize", "java": "fixedSize"},
    {"csharp": "GetRange", "java": "getRange"},
    {"csharp": "Repeat", "java": "repeat"},
    {"csharp": "Synchronized", "java": "synchronized_"},
    {"csharp": "TrimToSize", "java": "trimToSize"}
]},
{"type": "System.Collections.Hashtable", "methods": [
    {"csharp": "Add", "java": "add"},
    {"csharp": "Clone", "java": "clone"},
    {"csharp": "Contains", "java": "contains"},
    {"csharp": "ContainsKey", "java": "containsKey"},
    {"csharp": "ContainsValue", "java": "containsValue"},
    {"csharp": "Synchronized", "java": "synchronized_"}
]},
{"type": "System.Collections.Generic.List`1", "methods": [
    {"csharp": "Add", "java": "add"},
    {"csharp": "AddRange", "java": "addRange"},
    {"csharp": "AsReadOnly", "java": "asReadOnly"},
    {"csharp": "BinarySearch", "java": "binarySearch"},
    {"csharp": "ConvertAll", "java": "convertAll"},
    {"csharp": "Exists", "java": "exists"},
    {"csharp": "Find", "java": "find"},
    {"csharp": "FindAll", "java": "findAll"},
    {"csharp": "FindIndex", "java": "findIndex"},
    {"csharp": "FindLast", "java": "findLast"},
    {"csharp": "FindLastIndex", "java": "findLastIndex"},
    {"csharp": "ForEach", "java": "forEach"},
    {"csharp": "GetRange", "java": "getRange"},
    {"csharp": "InsertRange", "java": "insertRange"},
    {"csharp": "RemoveAll", "java": "removeAll"},
    {"csharp": "RemoveRange", "java": "removeRange"},
    {"csharp": "TrimExcess", "java": "trimExcess"},
    {"csharp": "TrueForAll", "java": "trueForAll"}
]},
{"type": "System.Collections.Generic.Dictionary`2", "methods": [
    {"csharp": "Add", "java": "add"},
    {"csharp": "EnsureCapacity", "java": "ensureCapacity"},
    {"csharp": "TryAdd", "java": "tryAdd"},
    {"csharp": "TryGetValue", "java": "tryGetValue"},
    {"csharp": "TrimExcess", "java": "trimExcess"}
]},
{"type": "System.Collections.Generic.HashSet`1", "methods": [
    {"csharp": "Add", "java": "add"},
    {"csharp": "ExceptWith", "java": "exceptWith"},
    {"csharp": "IntersectWith", "java": "intersectWith"},
    {"csharp": "IsProperSubsetOf", "java": "isProperSubsetOf"},
    {"csharp": "IsProperSupersetOf", "java": "isProperSupersetOf"},
    {"csharp": "IsSubsetOf", "java": "isSubsetOf"},
    {"csharp": "IsSupersetOf", "java": "isSupersetOf"},
    {"csharp": "Overlaps", "java": "overlaps"},
    {"csharp": "SetEquals", "java": "setEquals"},
    {"csharp": "SymmetricExceptWith", "java": "symmetricExceptWith"},
    {"csharp": "TrimExcess", "java": "trimExcess"},
    {"csharp": "UnionWith", "java": "unionWith"}
]},
{"type": "System.Collections.Generic.Queue`1", "methods": [
    {"csharp": "Dequeue", "java": "dequeue"},
    {"csharp": "Enqueue", "java": "enqueue"},
    {"csharp": "TryDequeue", "java": "tryDequeue"},
    {"csharp": "TryPeek", "java": "tryPeek"}
]},
{"type": "System.Collections.Generic.Stack`1", "methods": [
    {"csharp": "Pop", "java": "pop"},
    {"csharp": "Push", "java": "push"},
    {"csharp": "TryPop", "java": "tryPop"},
    {"csharp": "TryPeek", "java": "tryPeek"}
]},
{"type": "System.Collections.Generic.LinkedList`1", "methods": [
    {"csharp": "AddAfter", "java": "addAfter"},
    {"csharp": "AddBefore", "java": "addBefore"},
    {"csharp": "AddFirst", "java": "addFirst"},
    {"csharp": "AddLast", "java": "addLast"},
    {"csharp": "Find", "java": "find"},
    {"csharp": "FindLast", "java": "findLast"},
    {"csharp": "RemoveFirst", "java": "removeFirst"},
    {"csharp": "RemoveLast", "java": "removeLast"}
]},
{"type": "System.Collections.Generic.PriorityQueue`2", "methods": [
    {"csharp": "Dequeue", "java": "dequeue"},
    {"csharp": "DequeueRange", "java": "dequeueRange"},
    {"csharp": "Enqueue", "java": "enqueue"},
    {"csharp": "EnqueueRange", "java": "enqueueRange"},
    {"csharp": "TryDequeue", "java": "tryDequeue"},
    {"csharp": "TryPeek", "java": "tryPeek"}
]}
```

- [ ] **Step 4: Commit**

```bash
git add config/TypeMappings.json
git commit -m "Update TypeMappings.json with collection framework type and method mappings"
```

---

### Task 10: Final Verification

- [ ] **Step 1: Run full Java test suite**

Run: `cd java/csharptojava-compat && mvn test`
Expected: All tests pass

- [ ] **Step 2: Verify C# reflection tool output is complete**

Run: `cd tools/CollectionReflection && dotnet run`
Expected: All 3 output files generated successfully

- [ ] **Step 3: Verify TypeMappings.json is valid JSON**

Run: `python -m json.tool config/TypeMappings.json > /dev/null`
Expected: No error

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "Complete C# collection framework Java compat layer implementation"
```
