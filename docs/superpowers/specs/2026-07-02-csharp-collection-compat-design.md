# C# Collection Framework Java Compat Layer Design

Date: 2026-07-02

## Goal

Implement a complete Java compatibility layer for C# collection frameworks (System.Collections + System.Collections.Generic), using thin adapters over Java standard collections. Validate correctness via C# reflection-generated test oracles. Update TypeMappings.json.

## Scope

- **In**: System.Collections (non-generic) + System.Collections.Generic (generic) — all public interfaces and classes
- **Out**: System.Collections.Concurrent, System.Collections.Specialized, async interfaces (IAsyncEnumerable/IAsyncEnumerator), .NET 9 AlternateLookup, SynchronizedCollection/SynchronizedKeyedCollection (WCF-specific)

## Approach: Thin Adapter Pattern

Java interfaces map C# interfaces; concrete classes wrap Java standard collections internally. Rationale:
- Purpose is C#→Java code conversion compat, not rewriting collections from scratch
- Java standard collections are mature — thin adapters maximize correctness
- C#-specific semantics (e.g., ArrayList.IsFixedSize) are layered on top

## Naming Convention

- Package: `io.github.ningpp.compat` (consistent with existing code)
- Non-generic: `CSharpCollection`, `CSharpIList`, `CSharpIDictionary`, `CSharpArrayList`, `CSharpHashtable`, etc.
- Generic: `CSharpICollection<T>`, `CSharpIList<T>`, `CSharpIDictionary<K,V>`, `CSharpList<T>`, `CSharpDictionary<K,V>`, etc.
- Nested types: `CSharpDictionary.KeyCollection<K,V>`, `CSharpDictionary.ValueCollection<K,V>`

## Non-Generic Interfaces (System.Collections)

| C# Type | Java Type | Extends |
|---|---|---|
| IEnumerable | CSharpIterable | Iterable<Object> |
| IEnumerator | CSharpEnumerator (rewrite) | Iterator<Object> |
| ICollection | CSharpCollection (rewrite) | CSharpIterable |
| IList | CSharpIList (rewrite existing IList) | CSharpCollection |
| IDictionary | CSharpIDictionary (rewrite existing IDictionary) | CSharpCollection |
| IDictionaryEnumerator | CSharpDictEnumerator (rewrite) | CSharpEnumerator<Object> |
| IComparer | CSharpComparer | standalone interface |
| IEqualityComparer | CSharpEqualityComparer | standalone interface |
| IStructuralComparable | CSharpStructuralComparable | standalone interface |
| IStructuralEquatable | CSharpStructuralEquatable | standalone interface |

Skip: IHashCodeProvider, CaseInsensitiveHashCodeProvider (obsolete).

## Non-Generic Classes (System.Collections)

| C# Type | Java Type | Internal Delegate |
|---|---|---|
| ArrayList | CSharpArrayList | ArrayList<Object> |
| BitArray | CSharpBitArray | BitSet + extra logic |
| Hashtable | CSharpHashtable | LinkedHashMap<Object,Object> |
| Queue | CSharpQueue | LinkedList<Object> |
| Stack | CSharpObjStack | ArrayDeque<Object> |
| SortedList | CSharpObjSortedList | TreeMap<Object,Object> |
| DictionaryEntry | CSharpDictEntry (rewrite) | Map.Entry |
| Comparer | CSharpDefaultComparer | Comparator.naturalOrder() |
| CaseInsensitiveComparer | CSharpCaseInsensitiveComparer | String.CASE_INSENSITIVE_ORDER |
| CollectionBase | CSharpCollectionBase | abstract, internal ArrayList |
| DictionaryBase | CSharpDictionaryBase | abstract, internal Hashtable |
| ReadOnlyCollectionBase | CSharpReadOnlyCollectionBase | abstract, internal ArrayList |
| StructuralComparisons | CSharpStructuralComparisons | static factory |

## Generic Interfaces (System.Collections.Generic)

| C# Type | Java Type | Extends |
|---|---|---|
| IEnumerable<T> | CSharpIterable<T> | Iterable<T> |
| IEnumerator<T> | CSharpEnumerator<T> (rewrite) | Iterator<T>, CSharpEnumerator |
| ICollection<T> | CSharpICollection<T> | CSharpIterable<T> |
| IList<T> | CSharpIList<T> | CSharpICollection<T> |
| IDictionary<TKey,TValue> | CSharpIDictionary<K,V> | CSharpICollection<Map.Entry<K,V>> |
| IComparer<T> | CSharpComparer<T> | functional interface |
| IEqualityComparer<T> | CSharpEqualityComparer<T> (rewrite) | functional interface |
| IReadOnlyCollection<T> | CSharpReadOnlyCollection<T> | CSharpIterable<T> |
| IReadOnlyList<T> | CSharpReadOnlyList<T> | CSharpReadOnlyCollection<T> |
| IReadOnlyDictionary<TKey,TValue> | CSharpReadOnlyDict<K,V> | CSharpReadOnlyCollection<Map.Entry<K,V>> |
| IReadOnlySet<T> | CSharpReadOnlySet<T> | CSharpReadOnlyCollection<T> |
| ISet<T> | CSharpISet<T> | CSharpICollection<T> |

## Generic Classes (System.Collections.Generic)

| C# Type | Java Type | Internal Delegate |
|---|---|---|
| List<T> | CSharpList<T> | ArrayList<T> |
| Dictionary<TKey,TValue> | CSharpDictionary<K,V> | LinkedHashMap<K,V> |
| Dictionary.KeyCollection | CSharpDictionary.KeyCollection | ref parent dict |
| Dictionary.ValueCollection | CSharpDictionary.ValueCollection | ref parent dict |
| HashSet<T> | CSharpHashSet<T> | LinkedHashSet<T> (preserves insertion order) |
| SortedSet<T> | CSharpSortedSet<T> | TreeSet<T> |
| SortedDictionary<TKey,TValue> | CSharpSortedDict<K,V> | TreeMap<K,V> |
| SortedDictionary.KeyCollection | CSharpSortedDict.KeyCollection | ref parent |
| SortedDictionary.ValueCollection | CSharpSortedDict.ValueCollection | ref parent |
| SortedList<TKey,TValue> | CSharpSortedList<K,V> (rewrite) | TreeMap<K,V> + index array |
| Queue<T> | CSharpQueue<T> | java.util.LinkedList<T> |
| Stack<T> | CSharpStack<T> (rewrite) | ArrayDeque<T> |
| LinkedList<T> | CSharpLinkedList<T> (rewrite) | hand-written doubly-linked list (node access required) |
| LinkedListNode<T> | CSharpLinkedListNode<T> (rewrite) | node class |
| PriorityQueue<TElement,TPriority> | CSharpPriorityQueue<E,P> | java.util.PriorityQueue |
| KeyValuePair<TKey,TValue> | CSharpKeyValuePair<K,V> | value object |
| Comparer<T> | CSharpDefaultComparer<T> | static defaultInstance() |
| EqualityComparer<T> | CSharpDefaultEqualityComparer<T> | static defaultInstance() |
| ReferenceEqualityComparer | CSharpRefEqualityComparer | IdentityHashMap |
| KeyedByTypeCollection<TItem> | CSharpKeyedByTypeCollection<T> | ArrayList<T> + type keys |
| CollectionExtensions | CSharpCollectionExtensions | static utility methods |

### Not Implemented (with rationale)

- SynchronizedCollection/SynchronizedKeyedCollection/SynchronizedReadOnlyCollection: WCF-specific
- Dictionary.AlternateLookup / HashSet.AlternateLookup: .NET 9 new feature
- Enumerator structs: Java uses Iterator pattern, no struct equivalent needed
- OrderedDictionary<TKey,TValue>: .NET 9 new feature, can add later
- IAlternateEqualityComparer<TAlternate,T>: .NET 9 new, rarely used

## Reflection Tool: CollectionReflection

Location: `tools/CollectionReflection/`
Type: .NET 10 console app (following DateTimeReflection pattern)

### Outputs 3 files:

1. **collection-api-signatures.txt** — All type/member signatures for human review
2. **collection-test-data.json** — C# runtime examples: construct → add → query → remove → iterate, recording each step's result
3. **collection-constants.json** — Static constant values (e.g., Comparer.Default)

### Reflection targets:
- System.Collections: all types listed above
- System.Collections.Generic: all types listed above

### Test data generation strategy:
For each collection type:
- Construct instance (various constructors)
- Add/insert elements
- Query (Contains, IndexOf, Count, etc.)
- Remove elements
- Iterate (foreach, enumerator)
- Sort/order operations where applicable
- Record every operation's input and output as JSON

## TypeMappings.json Updates

### typeMappings additions (non-generic):
```
System.Collections.ArrayList → CSharpArrayList
System.Collections.BitArray → CSharpBitArray
System.Collections.Hashtable → CSharpHashtable
System.Collections.Queue → CSharpQueue
System.Collections.Stack → CSharpObjStack
System.Collections.SortedList → CSharpObjSortedList
System.Collections.DictionaryEntry → CSharpDictEntry
System.Collections.Comparer → CSharpDefaultComparer
System.Collections.CaseInsensitiveComparer → CSharpCaseInsensitiveComparer
System.Collections.CollectionBase → CSharpCollectionBase
System.Collections.DictionaryBase → CSharpDictionaryBase
System.Collections.ReadOnlyCollectionBase → CSharpReadOnlyCollectionBase
System.Collections.StructuralComparisons → CSharpStructuralComparisons
System.Collections.IEnumerable → CSharpIterable
System.Collections.IEnumerator → CSharpEnumerator
System.Collections.ICollection → CSharpCollection
System.Collections.IList → CSharpIList
System.Collections.IDictionary → CSharpIDictionary
System.Collections.IDictionaryEnumerator → CSharpDictEnumerator
System.Collections.IComparer → CSharpComparer
System.Collections.IEqualityComparer → CSharpEqualityComparer
System.Collections.IStructuralComparable → CSharpStructuralComparable
System.Collections.IStructuralEquatable → CSharpStructuralEquatable
```

### typeMappings additions (generic):
```
System.Collections.Generic.List<T> → CSharpList
System.Collections.Generic.Dictionary<TKey,TValue> → CSharpDictionary
System.Collections.Generic.HashSet<T> → CSharpHashSet
System.Collections.Generic.SortedSet<T> → CSharpSortedSet
System.Collections.Generic.SortedDictionary<TKey,TValue> → CSharpSortedDict
System.Collections.Generic.SortedList<TKey,TValue> → CSharpSortedList
System.Collections.Generic.Queue<T> → CSharpQueue
System.Collections.Generic.Stack<T> → CSharpStack
System.Collections.Generic.LinkedList<T> → CSharpLinkedList
System.Collections.Generic.LinkedListNode<T> → CSharpLinkedListNode
System.Collections.Generic.PriorityQueue<TElement,TPriority> → CSharpPriorityQueue
System.Collections.Generic.KeyValuePair<TKey,TValue> → CSharpKeyValuePair
System.Collections.Generic.Comparer<T> → CSharpDefaultComparer
System.Collections.Generic.EqualityComparer<T> → CSharpDefaultEqualityComparer
System.Collections.Generic.ReferenceEqualityComparer → CSharpRefEqualityComparer
System.Collections.Generic.KeyedByTypeCollection<TItem> → CSharpKeyedByTypeCollection
System.Collections.Generic.ICollection<T> → CSharpICollection
System.Collections.Generic.IList<T> → CSharpIList
System.Collections.Generic.IDictionary<TKey,TValue> → CSharpIDictionary
System.Collections.Generic.IEnumerable<T> → CSharpIterable
System.Collections.Generic.IEnumerator<T> → CSharpEnumerator
System.Collections.Generic.IComparer<T> → CSharpComparer
System.Collections.Generic.IEqualityComparer<T> → CSharpEqualityComparer
System.Collections.Generic.IReadOnlyCollection<T> → CSharpReadOnlyCollection
System.Collections.Generic.IReadOnlyList<T> → CSharpReadOnlyList
System.Collections.Generic.IReadOnlyDictionary<TKey,TValue> → CSharpReadOnlyDict
System.Collections.Generic.IReadOnlySet<T> → CSharpReadOnlySet
System.Collections.Generic.ISet<T> → CSharpISet
```

### methodMappings additions:
C# method name → Java method name for each collection type, e.g.:
- ArrayList.Add → add, ArrayList.AddRange → addAll, ArrayList.BinarySearch → binarySearch
- Hashtable.Add → put, Hashtable.ContainsKey → containsKey, Hashtable.ContainsValue → containsValue
- List<T>.Add → add, List<T>.AddRange → addAll, List<T>.BinarySearch → binarySearch
- Dictionary<TKey,TValue>.Add → put, Dictionary.TryGetValue → tryGetValue
- etc.

## Test Architecture

### Java test structure:
One test class per major type in `src/test/java/io/github/ningpp/compat/`:
- `CSharpArrayListTest`, `CSharpHashtableTest`, `CSharpQueueTest`, `CSharpObjStackTest`
- `CSharpListTest`, `CSharpDictionaryTest`, `CSharpHashSetTest`, `CSharpSortedSetTest`
- `CSharpSortedDictTest`, `CSharpSortedListTest`, `CSharpLinkedListTest`
- `CSharpPriorityQueueTest`, `CSharpBitArrayTest`
- Interface compliance tests: `CSharpCollectionTest`, `CSharpICollectionTest`, etc.

### Test validation:
- Read `collection-test-data.json` generated by C# reflection tool
- For each test case: construct Java collection with same inputs, execute same operations, assert outputs match C# oracle values
- Cover: construction, add, remove, query, iteration, sort, set operations, edge cases (empty, null, concurrent modification)

## Existing Code to Rewrite

The following existing files will be replaced:
- `CSharpCollection.java` (interface) — rewrite as non-generic ICollection
- `IList.java` (interface) — rename/rewrite as CSharpIList
- `IDictionary.java` (interface) — rename/rewrite as CSharpIDictionary
- `IDictionaryEnumerator.java` — rename/rewrite as CSharpDictEnumerator
- `DictionaryEntry.java` — rewrite as CSharpDictEntry
- `CSharpEnumerator.java` — rewrite to support both generic and non-generic
- `CSharpStack.java` — rewrite as generic Stack<T>
- `KeyedCollection.java` — evaluate and integrate
- `LinkedListWithNodes.java` / `LinkedListNode.java` — rewrite as CSharpLinkedList/CSharpLinkedListNode
- `SortedList.java` — rewrite as generic CSharpSortedList<K,V>
- `CSharpArray.java` — keep (not a collection wrapper, array utility)

## Success Criteria

1. All Java collection classes compile and pass tests
2. C# reflection tool generates API signatures + test data successfully
3. Java test outputs match C# oracle data for all test cases
4. TypeMappings.json updated with all collection mappings
5. `mvn test` passes in `java/csharptojava-compat`
