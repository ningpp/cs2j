# CSharpStack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `CSharpStack<T>` compat class with LIFO iteration order to fix the semantic mismatch between C# `Stack<T>` and Java `java.util.Stack`.

**Architecture:** Full replacement class using `ArrayDeque<T>` as internal storage. `iterator()` returns LIFO order via `descendingIterator()`, matching C# `Stack<T>.GetEnumerator()` semantics. No converter-side changes needed — the LIFO iterator makes translated `foreach`, `Reverse()`, and `GetEnumerator()` calls naturally correct.

**Tech Stack:** Java 25, JUnit Jupiter 5.11.4, Maven, ArrayDeque

---

## File Structure

| Action | File | Responsibility |
|---|---|---|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java` | C# Stack<T> compat class with LIFO iteration |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpStackTest.java` | Tests for all CSharpStack methods |
| Modify | `config/TypeMappings.json` | Update type mapping + add method mappings |

---

### Task 1: Create CSharpStack.java — Core Methods

**Files:**
- Create: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java`

- [ ] **Step 1: Write CSharpStack.java with fields, constructors, and core methods**

```java
package io.github.ningpp.compat;

import java.util.*;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;

public class CSharpStack<T> implements Iterable<T> {
    private ArrayDeque<T> deque;

    public CSharpStack() {
        this.deque = new ArrayDeque<>();
    }

    public CSharpStack(int capacity) {
        this.deque = new ArrayDeque<>(capacity);
    }

    public CSharpStack(Collection<? extends T> c) {
        this.deque = new ArrayDeque<>(c.size());
        for (T item : c) {
            deque.addLast(item);
        }
    }

    public void push(T item) {
        deque.addLast(item);
    }

    public T pop() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException();
        }
        return deque.removeLast();
    }

    public T peek() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException();
        }
        return deque.peekLast();
    }

    public int size() {
        return deque.size();
    }

    public boolean isEmpty() {
        return deque.isEmpty();
    }

    public void clear() {
        deque.clear();
    }

    public boolean contains(Object item) {
        return deque.contains(item);
    }
}
```

- [ ] **Step 2: Compile to verify no errors**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 2: Add LIFO Iterator and Stream Support

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java`

- [ ] **Step 1: Add iterator(), spliterator(), and stream() methods to CSharpStack**

Add these methods inside the `CSharpStack` class, after the `contains` method:

```java
    @Override
    public Iterator<T> iterator() {
        return deque.descendingIterator();
    }

    @Override
    public Spliterator<T> spliterator() {
        return Spliterators.spliteratorUnknownSize(iterator(), Spliterator.ORDERED);
    }

    public Stream<T> stream() {
        return StreamSupport.stream(spliterator(), false);
    }
```

- [ ] **Step 2: Compile to verify**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 3: Add ToArray and CopyTo Methods

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java`

- [ ] **Step 1: Add toArray(), toArray(T[]), and copyTo() methods**

Add these methods inside the `CSharpStack` class, after the `stream()` method:

```java
    public Object[] toArray() {
        Object[] result = new Object[deque.size()];
        int i = 0;
        for (T item : this) {
            result[i++] = item;
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    public T[] toArray(T[] a) {
        int size = deque.size();
        T[] result = a.length >= size
            ? a
            : (T[]) java.lang.reflect.Array.newInstance(a.getClass().getComponentType(), size);
        int i = 0;
        for (T item : this) {
            result[i++] = item;
        }
        if (result.length > size) {
            result[size] = null;
        }
        return result;
    }

    public void copyTo(T[] array, int arrayIndex) {
        Objects.checkIndex(arrayIndex, array.length + 1);
        if (arrayIndex + deque.size() > array.length) {
            throw new IndexOutOfBoundsException(
                "Destination array is not long enough to copy all elements.");
        }
        int i = arrayIndex;
        for (T item : this) {
            array[i++] = item;
        }
    }
```

- [ ] **Step 2: Compile to verify**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 4: Add TryPeek, TryPop, TrimExcess, GetEnumerator

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java`

- [ ] **Step 1: Add tryPeek, tryPop, trimExcess, and getEnumerator methods**

Add these methods inside the `CSharpStack` class, after the `copyTo()` method:

```java
    public boolean tryPeek(ObjectHolder<T> holder) {
        if (deque.isEmpty()) {
            return false;
        }
        holder.value = deque.peekLast();
        return true;
    }

    public boolean tryPop(ObjectHolder<T> holder) {
        if (deque.isEmpty()) {
            return false;
        }
        holder.value = deque.removeLast();
        return true;
    }

    public void trimExcess() {
        if (deque.size() < deque.size()) {
            ArrayDeque<T> newDeque = new ArrayDeque<>(deque);
            deque = newDeque;
        }
    }

    public void trimExcess(int capacity) {
        if (capacity < deque.size()) {
            throw new IllegalArgumentException(
                "capacity must be >= current size");
        }
        ArrayDeque<T> newDeque = new ArrayDeque<>(capacity);
        newDeque.addAll(deque);
        deque = newDeque;
    }

    public CSharpEnumerator<T> getEnumerator() {
        return CSharpEnumerator.from(iterator());
    }
```

- [ ] **Step 2: Compile to verify**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 5: Add equals and hashCode

**Files:**
- Modify: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java`

- [ ] **Step 1: Add equals() and hashCode() methods**

Add these methods inside the `CSharpStack` class, after the `getEnumerator()` method:

```java
    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpStack)) return false;
        CSharpStack<?> other = (CSharpStack<?>) obj;
        if (this.size() != other.size()) return false;
        Iterator<T> it1 = this.iterator();
        Iterator<?> it2 = other.iterator();
        while (it1.hasNext() && it2.hasNext()) {
            if (!Objects.equals(it1.next(), it2.next())) return false;
        }
        return true;
    }

    @Override
    public int hashCode() {
        int h = 1;
        for (T item : this) {
            h = 31 * h + Objects.hashCode(item);
        }
        return h;
    }
```

- [ ] **Step 2: Compile to verify**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS

---

### Task 6: Write CSharpStackTest.java — Constructor and Core Method Tests

**Files:**
- Create: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpStackTest.java`

- [ ] **Step 1: Write test file with constructor and core method tests**

```java
package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

import java.util.Arrays;
import java.util.List;
import java.util.NoSuchElementException;

class CSharpStackTest {

    @Test
    void defaultConstructor_createsEmptyStack() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }

    @Test
    void capacityConstructor_createsEmptyStack() {
        CSharpStack<String> s = new CSharpStack<>(100);
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }

    @Test
    void collectionConstructor_pushesInOrder() {
        List<String> items = Arrays.asList("A", "B", "C");
        CSharpStack<String> s = new CSharpStack<>(items);
        assertEquals(3, s.size());
        assertEquals("C", s.peek());
    }

    @Test
    void pushPopPeek_basicOperations() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(10);
        s.push(20);
        s.push(30);
        assertEquals(30, s.peek());
        assertEquals(30, s.pop());
        assertEquals(20, s.peek());
        assertEquals(20, s.pop());
        assertEquals(10, s.pop());
        assertTrue(s.isEmpty());
    }

    @Test
    void pop_emptyStack_throws() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertThrows(NoSuchElementException.class, s::pop);
    }

    @Test
    void peek_emptyStack_throws() {
        CSharpStack<Integer> s = new CSharpStack<>();
        assertThrows(NoSuchElementException.class, s::peek);
    }

    @Test
    void size_isEmpty_reflectState() {
        CSharpStack<String> s = new CSharpStack<>();
        assertTrue(s.isEmpty());
        assertEquals(0, s.size());
        s.push("x");
        assertFalse(s.isEmpty());
        assertEquals(1, s.size());
        s.pop();
        assertTrue(s.isEmpty());
    }

    @Test
    void contains_findsElements() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("hello");
        s.push("world");
        assertTrue(s.contains("hello"));
        assertTrue(s.contains("world"));
        assertFalse(s.contains("missing"));
        assertFalse(s.contains(null));
    }

    @Test
    void clear_emptiesStack() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(1);
        s.push(2);
        s.clear();
        assertEquals(0, s.size());
        assertTrue(s.isEmpty());
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn test -pl . -Dtest=CSharpStackTest -q`
Expected: All tests PASS

---

### Task 7: Add LIFO Iterator and Stream Tests

**Files:**
- Modify: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpStackTest.java`

- [ ] **Step 1: Add iterator and stream tests**

Add these test methods to `CSharpStackTest`:

```java
    @Test
    void iterator_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        List<String> result = new java.util.ArrayList<>();
        for (String item : s) {
            result.add(item);
        }
        assertEquals(Arrays.asList("C", "B", "A"), result);
    }

    @Test
    void stream_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        List<String> result = s.stream().toList();
        assertEquals(Arrays.asList("C", "B", "A"), result);
    }
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn test -pl . -Dtest=CSharpStackTest -q`
Expected: All tests PASS

---

### Task 8: Add ToArray, CopyTo, TryPeek, TryPop, TrimExcess, GetEnumerator, Equals, HashCode Tests

**Files:**
- Modify: `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpStackTest.java`

- [ ] **Step 1: Add remaining method tests**

Add these test methods to `CSharpStackTest`:

```java
    @Test
    void toArray_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        Object[] arr = s.toArray();
        assertArrayEquals(new Object[]{"C", "B", "A"}, arr);
    }

    @Test
    void toArrayTyped_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        String[] arr = s.toArray(new String[0]);
        assertArrayEquals(new String[]{"C", "B", "A"}, arr);
    }

    @Test
    void toArrayTyped_existingArrayUsed() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(1);
        s.push(2);
        Integer[] arr = new Integer[5];
        Integer[] result = s.toArray(arr);
        assertSame(arr, result);
        assertEquals(2, result[0]);
        assertEquals(1, result[1]);
        assertNull(result[2]);
    }

    @Test
    void copyTo_copiesLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        String[] arr = new String[5];
        s.copyTo(arr, 1);
        assertNull(arr[0]);
        assertEquals("C", arr[1]);
        assertEquals("B", arr[2]);
        assertEquals("A", arr[3]);
        assertNull(arr[4]);
    }

    @Test
    void tryPeek_succeedsOnNonEmpty() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("hello");
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertTrue(s.tryPeek(holder));
        assertEquals("hello", holder.value);
        assertEquals(1, s.size());
    }

    @Test
    void tryPeek_failsOnEmpty() {
        CSharpStack<String> s = new CSharpStack<>();
        ObjectHolder<String> holder = new ObjectHolder<>();
        assertFalse(s.tryPeek(holder));
    }

    @Test
    void tryPop_succeedsOnNonEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        s.push(42);
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertTrue(s.tryPop(holder));
        assertEquals(42, holder.value);
        assertTrue(s.isEmpty());
    }

    @Test
    void tryPop_failsOnEmpty() {
        CSharpStack<Integer> s = new CSharpStack<>();
        ObjectHolder<Integer> holder = new ObjectHolder<>();
        assertFalse(s.tryPop(holder));
    }

    @Test
    void trimExcess_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        for (int i = 0; i < 100; i++) {
            s.push(i);
        }
        s.trimExcess();
        assertEquals(100, s.size());
        assertEquals(99, s.peek());
    }

    @Test
    void trimExcessWithCapacity_preservesElements() {
        CSharpStack<Integer> s = new CSharpStack<>();
        for (int i = 0; i < 10; i++) {
            s.push(i);
        }
        s.trimExcess(50);
        assertEquals(10, s.size());
        assertEquals(9, s.peek());
    }

    @Test
    void getEnumerator_returnsLIFOOrder() {
        CSharpStack<String> s = new CSharpStack<>();
        s.push("A");
        s.push("B");
        s.push("C");
        CSharpEnumerator<String> e = s.getEnumerator();
        assertTrue(e.moveNext());
        assertEquals("C", e.getCurrent());
        assertTrue(e.moveNext());
        assertEquals("B", e.getCurrent());
        assertTrue(e.moveNext());
        assertEquals("A", e.getCurrent());
        assertFalse(e.moveNext());
    }

    @Test
    void equals_sameElementsSameOrder() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertEquals(s1, s2);
    }

    @Test
    void equals_differentOrder_notEqual() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("B");
        s2.push("A");
        assertNotEquals(s1, s2);
    }

    @Test
    void equals_differentSize_notEqual() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertNotEquals(s1, s2);
    }

    @Test
    void hashCode_consistentWithEquals() {
        CSharpStack<String> s1 = new CSharpStack<>();
        s1.push("A");
        s1.push("B");
        CSharpStack<String> s2 = new CSharpStack<>();
        s2.push("A");
        s2.push("B");
        assertEquals(s1.hashCode(), s2.hashCode());
    }
```

- [ ] **Step 2: Run all tests**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn test -pl . -Dtest=CSharpStackTest -q`
Expected: All tests PASS

---

### Task 9: Update TypeMappings.json

**Files:**
- Modify: `config/TypeMappings.json`

- [ ] **Step 1: Update the Stack type mapping**

Find the existing Stack type mapping at approximately line 654-660:

```json
                        {
                            "csharp":  "System.Collections.Generic.Stack`1",
                            "java":  "Stack",
                            "imports":  [
                                            "java.util.Stack"
                                        ]
                        },
```

Replace with:

```json
                        {
                            "csharp":  "System.Collections.Generic.Stack`1",
                            "java":  "CSharpStack",
                            "imports":  [
                                            "io.github.ningpp.compat.CSharpStack"
                                        ]
                        },
```

- [ ] **Step 2: Add new method mappings**

Find the existing Stack method mappings at approximately line 2911-2925:

```json
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "Push",
                              "javaMethod":  "push"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "Pop",
                              "javaMethod":  "pop"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "Peek",
                              "javaMethod":  "peek"
                          },
```

Add the following new method mappings immediately after the `Peek` mapping (after line ~2925):

```json
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "Contains",
                              "javaMethod":  "contains"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "Clear",
                              "javaMethod":  "clear"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "ToArray",
                              "javaMethod":  "toArray"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "CopyTo",
                              "javaMethod":  "copyTo"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "TryPeek",
                              "javaMethod":  "tryPeek"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "TryPop",
                              "javaMethod":  "tryPop"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "TrimExcess",
                              "javaMethod":  "trimExcess"
                          },
                          {
                              "type":  "System.Collections.Generic.Stack`1",
                              "method":  "GetEnumerator",
                              "javaMethod":  "getEnumerator"
                          },
```

- [ ] **Step 3: Verify JSON is valid**

Run: `cd d:\code\cs2j && python -c "import json; json.load(open('config/TypeMappings.json', encoding='utf-8')); print('Valid JSON')"`
Expected: "Valid JSON"

---

### Task 10: Final Verification

- [ ] **Step 1: Run full test suite**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn test -q`
Expected: All tests PASS

- [ ] **Step 2: Verify CSharpStack compiles and all methods are accessible**

Run: `cd d:\code\cs2j\java\csharptojava-compat && mvn compile -q`
Expected: BUILD SUCCESS
