# SortedList Compat Class Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a `SortedList<K, V>` Java compat class that wraps `TreeMap` and adds index-based access, replacing the current direct `TreeMap` mapping.

**Architecture:** TreeMap wrapper in `io.github.ningpp.compat` package. Key-based operations delegate to TreeMap (O(log n)). Index-based operations build sorted lists on demand (O(n)). Follows the same pattern as `LinkedListWithNodes`.

**Tech Stack:** Java 25, JUnit 5, Maven

---

## File Structure

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/SortedList.java` | Compat class wrapping TreeMap |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/SortedListTest.java` | Unit tests |
| Modify | `config/TypeMappings.json:669-673` | Type mapping: TreeMap -> SortedList |
| Modify | `config/TypeMappings.json:2833-2837` | Remove Count -> size method mapping |

---

### Task 1: Write SortedListTest.java (failing tests first)

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/SortedListTest.java`

- [ ] **Step 1: Write the test file**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import static org.junit.jupiter.api.Assertions.*;

class SortedListTest {

    // ---- Construction ----

    @Test
    void defaultConstructor() {
        SortedList<String, Integer> list = new SortedList<>();
        assertEquals(0, list.size());
        assertTrue(list.isEmpty());
    }

    @Test
    void comparatorConstructor() {
        SortedList<String, Integer> list = new SortedList<>(Comparator.reverseOrder());
        list.put("b", 2);
        list.put("a", 1);
        assertEquals(List.of("b", "a"), list.keys());
    }

    @Test
    void mapConstructor() {
        SortedList<String, Integer> list = new SortedList<>(Map.of("c", 3, "a", 1, "b", 2));
        assertEquals(3, list.size());
        assertEquals(List.of("a", "b", "c"), list.keys());
    }

    // ---- Core operations ----

    @Test
    void putAndGet() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("b", 2);
        list.put("a", 1);
        list.put("c", 3);
        assertEquals(1, list.get("a"));
        assertEquals(2, list.get("b"));
        assertEquals(3, list.get("c"));
    }

    @Test
    void put_overwritesExisting() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("a", 10);
        assertEquals(10, list.get("a"));
        assertEquals(1, list.size());
    }

    @Test
    void containsKey() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertTrue(list.containsKey("a"));
        assertFalse(list.containsKey("z"));
    }

    @Test
    void containsValue() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertTrue(list.containsValue(1));
        assertFalse(list.containsValue(99));
    }

    @Test
    void remove_byKey() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        list.remove("a");
        assertEquals(1, list.size());
        assertFalse(list.containsKey("a"));
        assertNull(list.get("a"));
    }

    @Test
    void clear_removesAll() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        list.clear();
        assertTrue(list.isEmpty());
        assertEquals(0, list.size());
    }

    @Test
    void putAll_bulkInsert() {
        SortedList<String, Integer> list = new SortedList<>();
        list.putAll(Map.of("c", 3, "a", 1, "b", 2));
        assertEquals(3, list.size());
        assertEquals(1, list.get("a"));
    }

    // ---- Index-based access ----

    @Test
    void keys_returnsSortedList() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        List<String> keys = list.keys();
        assertEquals(List.of("a", "b", "c"), keys);
    }

    @Test
    void values_returnsSortedValues() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        List<Integer> values = list.values();
        assertEquals(List.of(10, 20, 30), values);
    }

    @Test
    void getKeyAtIndex() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        assertEquals("a", list.getKeyAtIndex(0));
        assertEquals("b", list.getKeyAtIndex(1));
        assertEquals("c", list.getKeyAtIndex(2));
    }

    @Test
    void getValueAtIndex() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(10, list.getValueAtIndex(0));
        assertEquals(20, list.getValueAtIndex(1));
        assertEquals(30, list.getValueAtIndex(2));
    }

    @Test
    void indexOfKey() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(0, list.indexOfKey("a"));
        assertEquals(1, list.indexOfKey("b"));
        assertEquals(2, list.indexOfKey("c"));
        assertEquals(-1, list.indexOfKey("z"));
    }

    @Test
    void indexOfValue() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 30);
        list.put("a", 10);
        list.put("b", 20);
        assertEquals(0, list.indexOfValue(10));
        assertEquals(1, list.indexOfValue(20));
        assertEquals(2, list.indexOfValue(30));
        assertEquals(-1, list.indexOfValue(99));
    }

    // ---- Extended operations ----

    @Test
    void tryGetValue_existing() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertTrue(list.tryGetValue("a", holder));
        assertEquals(1, holder.value);
    }

    @Test
    void tryGetValue_missing() {
        SortedList<String, Integer> list = new SortedList<>();
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertFalse(list.tryGetValue("z", holder));
        assertNull(holder.value);
    }

    @Test
    void removeAt() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("c", 3);
        list.put("a", 1);
        list.put("b", 2);
        list.removeAt(1); // remove "b"
        assertEquals(2, list.size());
        assertEquals(List.of("a", "c"), list.keys());
    }

    @Test
    void capacity_returnsSize() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        assertEquals(1, list.capacity());
    }

    @Test
    void ensureCapacity_noOp() {
        SortedList<String, Integer> list = new SortedList<>();
        list.ensureCapacity(100); // should not throw
        assertEquals(0, list.size());
    }

    // ---- Edge cases ----

    @Test
    void emptyList_keysReturnsEmpty() {
        SortedList<String, Integer> list = new SortedList<>();
        assertTrue(list.keys().isEmpty());
        assertTrue(list.values().isEmpty());
    }

    @Test
    void singleElement() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("only", 42);
        assertEquals("only", list.getKeyAtIndex(0));
        assertEquals(42, list.getValueAtIndex(0));
        assertEquals(0, list.indexOfKey("only"));
    }

    @Test
    void comparator_customOrder() {
        // Reverse natural order
        SortedList<String, Integer> list = new SortedList<>(Comparator.reverseOrder());
        list.put("a", 1);
        list.put("b", 2);
        list.put("c", 3);
        assertEquals(List.of("c", "b", "a"), list.keys());
        assertEquals(List.of(3, 2, 1), list.values());
    }

    // ---- Java interop ----

    @Test
    void entrySet_returnsEntries() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(2, list.entrySet().size());
    }

    @Test
    void keySet_returnsKeys() {
        SortedList<String, Integer> list = new SortedList<>();
        list.put("a", 1);
        list.put("b", 2);
        assertEquals(2, list.keySet().size());
        assertTrue(list.keySet().contains("a"));
    }

    @Test
    void comparator_returnsNullForNaturalOrder() {
        SortedList<String, Integer> list = new SortedList<>();
        assertNull(list.comparator()); // TreeMap returns null for natural ordering
    }

    @Test
    void comparator_returnsCustomComparator() {
        Comparator<String> cmp = Comparator.reverseOrder();
        SortedList<String, Integer> list = new SortedList<>(cmp);
        assertSame(cmp, list.comparator());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd d:/code/cs2j/java/csharptojava-compat && mvn test -pl . -Dtest=SortedListTest -q 2>&1 | tail -5`

Expected: Compilation error — `SortedList` class does not exist.

---

### Task 2: Implement SortedList.java

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/SortedList.java`

- [ ] **Step 1: Write the implementation**

```java
package io.github.ningpp.compat;

import java.util.*;

/**
 * C# SortedList&lt;TKey, TValue&gt; compat.
 * Wraps TreeMap for sorted key-value storage with index-based access.
 * Generated by CSharpToJava converter.
 */
public class SortedList<K extends Comparable<K>, V> {
    private final TreeMap<K, V> map;

    public SortedList() {
        this.map = new TreeMap<>();
    }

    public SortedList(Comparator<K> comparer) {
        this.map = new TreeMap<>(comparer);
    }

    public SortedList(Map<K, V> map) {
        this.map = new TreeMap<>(map);
    }

    // ---- Core (delegate to TreeMap) ----

    public int size() {
        return map.size();
    }

    public boolean isEmpty() {
        return map.isEmpty();
    }

    public boolean containsKey(K key) {
        return map.containsKey(key);
    }

    public boolean containsValue(V value) {
        return map.containsValue(value);
    }

    public V get(K key) {
        return map.get(key);
    }

    public V put(K key, V value) {
        return map.put(key, value);
    }

    public V remove(K key) {
        return map.remove(key);
    }

    public void clear() {
        map.clear();
    }

    public void putAll(Map<K, V> m) {
        map.putAll(m);
    }

    public Comparator<K> comparator() {
        return map.comparator();
    }

    public Set<Map.Entry<K, V>> entrySet() {
        return map.entrySet();
    }

    public Set<K> keySet() {
        return map.keySet();
    }

    public void forEach(java.util.function.BiConsumer<K, V> action) {
        map.forEach(action);
    }

    // ---- Index-based access ----

    public List<K> keys() {
        return new ArrayList<>(map.keySet());
    }

    public List<V> values() {
        return new ArrayList<>(map.values());
    }

    public K getKeyAtIndex(int index) {
        return keys().get(index);
    }

    public V getValueAtIndex(int index) {
        return values().get(index);
    }

    public int indexOfKey(K key) {
        int i = 0;
        for (K k : map.keySet()) {
            if (Objects.equals(k, key)) return i;
            i++;
        }
        return -1;
    }

    public int indexOfValue(V value) {
        int i = 0;
        for (V v : map.values()) {
            if (Objects.equals(v, value)) return i;
            i++;
        }
        return -1;
    }

    // ---- Extended ----

    public boolean tryGetValue(K key, ObjectHolder<V> holder) {
        V val = map.get(key);
        if (val != null || map.containsKey(key)) {
            holder.value = val;
            return true;
        }
        return false;
    }

    public void removeAt(int index) {
        K key = getKeyAtIndex(index);
        map.remove(key);
    }

    public int capacity() {
        return size();
    }

    public void ensureCapacity(int capacity) {
        // No-op: TreeMap has no capacity concept
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `cd d:/code/cs2j/java/csharptojava-compat && mvn test -pl . -Dtest=SortedListTest -q`

Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
cd d:/code/cs2j
git add java/csharptojava-compat/src/main/java/io/github/ningpp/compat/SortedList.java java/csharptojava-compat/src/test/java/io/github/ningpp/compat/SortedListTest.java
git commit -m "feat: add SortedList compat class wrapping TreeMap with index-based access"
```

---

### Task 3: Update TypeMappings.json

**Files:**
- Modify: `config/TypeMappings.json:669-673` — type mapping
- Modify: `config/TypeMappings.json:2833-2837` — remove method mapping

- [ ] **Step 1: Update the type mapping**

Change lines 668-674 from:

```json
{
    "csharp":  "System.Collections.Generic.SortedList`2",
    "java":  "TreeMap",
    "imports":  [
                    "java.util.TreeMap"
                ]
}
```

To:

```json
{
    "csharp":  "System.Collections.Generic.SortedList`2",
    "java":  "SortedList",
    "imports":  [
                    "io.github.ningpp.compat.SortedList"
                ]
}
```

- [ ] **Step 2: Remove the Count -> size method mapping**

Delete lines 2833-2837:

```json
{
    "type":  "System.Collections.Generic.SortedList`2",
    "method":  "Count",
    "javaMethod":  "size"
}
```

- [ ] **Step 3: Verify the .NET solution still builds**

Run: `cd d:/code/cs2j && dotnet build -q`

Expected: Build succeeds (TypeMappings.json is just configuration, no compilation impact).

- [ ] **Step 4: Commit**

```bash
cd d:/code/cs2j
git add config/TypeMappings.json
git commit -m "feat: map SortedList to compat class instead of TreeMap"
```

---

### Task 4: Run full test suite

- [ ] **Step 1: Run all compat tests**

Run: `cd d:/code/cs2j/java/csharptojava-compat && mvn test -q`

Expected: All tests pass (including the new SortedListTest and all existing tests).

- [ ] **Step 2: Run all .NET tests**

Run: `cd d:/code/cs2j && dotnet test --no-restore -q`

Expected: All tests pass.

- [ ] **Step 3: Final commit if needed**

If any fixes were needed in prior steps, commit them here.
