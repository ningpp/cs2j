# SortedList Compat Class Design

## Problem

C#'s `SortedList<TKey, TValue>` supports access by both key and integer index. The current type mapping sends it to Java's `TreeMap`, which only supports key-based access. Index-based operations (`Keys[i]`, `Values[i]`, `IndexOfKey`, `IndexOfValue`) are not available.

## Solution

Create a `SortedList<K, V>` compat class in `io.github.ningpp.compat` that wraps `TreeMap<K, V>` and adds index-based access methods.

## Approach

**TreeMap wrapper with on-demand index lists.** Key-based operations delegate to TreeMap (O(log n)). Index-based operations build lists from the TreeMap's sorted entries on demand (O(n)). This is the simplest approach and follows existing compat patterns (e.g., `LinkedListWithNodes`).

### Performance Tradeoffs

| Operation | C# SortedList | Compat SortedList (Java) |
|-----------|---------------|--------------------------|
| Key lookup | O(log n) | O(log n) |
| Put/Remove | O(n) | O(log n) |
| Index access | O(1) | O(n) |
| IndexOfKey | O(log n) | O(n) |

Java implementation is faster for mutation, slower for index access. Acceptable for a converter compat layer.

## Class Location

- **File**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/SortedList.java`
- **Test**: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/SortedListTest.java`
- **Package**: `io.github.ningpp.compat`

## API Surface

### Core (delegate to TreeMap)

| Method | Description |
|--------|-------------|
| `SortedList()` | Default constructor |
| `SortedList(Comparator<K>)` | Constructor with custom comparator |
| `SortedList(Map<K, V>)` | Constructor from existing map |
| `int size()` | Number of entries |
| `boolean isEmpty()` | True if empty |
| `boolean containsKey(K key)` | Key exists |
| `boolean containsValue(V value)` | Value exists |
| `V get(K key)` | Get value by key |
| `V put(K key, V value)` | Insert or update |
| `V remove(K key)` | Remove by key |
| `void clear()` | Remove all entries |
| `void putAll(Map<K, V>)` | Bulk insert |
| `Comparator<K> comparator()` | Get the comparator |
| `Set<Map.Entry<K,V>> entrySet()` | Entry set for iteration |
| `Set<K> keySet()` | Key set for iteration |
| `void forEach(BiConsumer<K,V>)` | Java-style iteration |

### Index-based (new, the gap TreeMap can't fill)

| Method | Description |
|--------|-------------|
| `List<K> keys()` | Sorted keys as list (supports `list.get(i)`) |
| `List<V> values()` | Sorted values as list (supports `list.get(i)`) |
| `K getKeyAtIndex(int index)` | Key at position |
| `V getValueAtIndex(int index)` | Value at position |
| `int indexOfKey(K key)` | Position of key, or -1 |
| `int indexOfValue(V value)` | Position of value, or -1 |

### Extended

| Method | Description |
|--------|-------------|
| `boolean tryGetValue(K key, ObjectHolder<V> holder)` | Maps C#'s `out` pattern |
| `void removeAt(int index)` | Remove entry at index position |
| `int capacity()` | No-op stub (returns `size()`) |
| `void ensureCapacity(int)` | No-op stub |

### Generic Constraint

`K extends Comparable<K>` — matches C#'s requirement that `TKey` implements `IComparable<TKey>`. The `Comparator<K>` constructor overload handles custom comparators.

## Converter Changes

### Type Mapping (`config/TypeMappings.json`)

Update the `SortedList`2` entry from:

```json
{
    "csharp": "System.Collections.Generic.SortedList`2",
    "java": "TreeMap",
    "imports": ["java.util.TreeMap"]
}
```

To:

```json
{
    "csharp": "System.Collections.Generic.SortedList`2",
    "java": "SortedList",
    "imports": ["io.github.ningpp.compat.SortedList"]
}
```

Remove the method mapping:

```json
{
    "type": "System.Collections.Generic.SortedList`2",
    "method": "Count",
    "javaMethod": "size"
}
```

The compat class provides `size()` natively, so this mapping is no longer needed.

### Type Recognition

Update `ExpressionTransformerHelpers.cs` (line ~694) and `LinqRewriter.cs` (line ~1145) if they check for `TreeMap` specifically when identifying dictionary types. They should recognize the compat `SortedList` as a dictionary type too.

## Testing

Unit tests in `SortedListTest.java` covering:
- Construction (default, comparator, from map)
- Put/get/remove by key
- Index-based access (`keys()`, `values()`, `getKeyAtIndex`, `getValueAtIndex`)
- `indexOfKey`, `indexOfValue`
- `tryGetValue` with `ObjectHolder`
- `removeAt`
- Edge cases: empty list, single element, duplicate values
