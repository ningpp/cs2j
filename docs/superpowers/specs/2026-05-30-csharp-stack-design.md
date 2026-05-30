# CSharpStack Design

## Goal

Add `io.github.ningpp.compat.CSharpStack` to the compat library so that C# code using `System.Collections.Generic.Stack<T>` can be correctly converted to Java with full semantic fidelity, especially iteration order.

## Problem

Current `TypeMappings.json` maps `System.Collections.Generic.Stack<T>` → `java.util.Stack` with method-level mappings `Push` → `push`, `Pop` → `pop`, `Peek` → `peek`, `Count` → `size`. This produces incorrect iteration behavior:

| C# Behavior | Java `java.util.Stack` Behavior | Issue |
|---|---|---|
| `foreach` iterates top-to-bottom (LIFO) | `iterator()` iterates bottom-to-top (insertion order) | **Iteration order reversed** |
| `stack.Reverse()` (LINQ) restores bottom-to-top | `Collections.reverse(...)` on already-bottom-to-top order | **Double reversal** |
| `GetEnumerator()` returns LIFO enumerator | `iterator()` returns FIFO iterator | **Enumerator order reversed** |
| `ToArray()` returns LIFO-ordered array | `toArray()` returns FIFO-ordered array | **Array order reversed** |

The cs2j converter does not distinguish between these traversal semantics and mechanically translates calls, producing incorrect Java code.

## Approach: Full Replacement Class with LIFO Iterator

`CSharpStack` is a standalone class (not extending `java.util.Stack`) that uses `ArrayDeque<T>` as internal storage. Its `iterator()` returns **LIFO order** (top-to-bottom), matching C# `Stack<T>.GetEnumerator()` semantics exactly. This eliminates the need for any converter-side special handling — translated `foreach`, `Reverse()`, and `GetEnumerator()` calls all produce correct results naturally.

Design pattern: same as `LinkedListWithNodes` (full replacement class in `io.github.ningpp.compat`).

## Class: `io.github.ningpp.compat.CSharpStack<T>`

### Fields

- `private ArrayDeque<T> deque` — internal storage. `addLast()` = Push, `peekLast()` = Peek, `removeLast()` = Pop. Not `final` because `trimExcess()` replaces it.

### Constructors

- `CSharpStack()` — default, delegates to `new ArrayDeque<>()`
- `CSharpStack(int capacity)` — delegates to `new ArrayDeque<>(capacity)`
- `CSharpStack(Collection<? extends T> c)` — iterates `c` and pushes each element, preserving C# constructor semantics (elements are pushed in iteration order, so the last element of the collection ends up on top)

### Core Methods

| C# Method | Java Method | Implementation |
|---|---|---|
| `Push(T item)` | `push(T item)` | `deque.addLast(item)` |
| `Pop()` | `pop()` | `deque.removeLast()`; throws `NoSuchElementException` if empty |
| `Peek()` | `peek()` | `deque.peekLast()`; throws `NoSuchElementException` if empty |
| `Count` | `size()` | `deque.size()` |
| `Clear()` | `clear()` | `deque.clear()` |
| `Contains(T item)` | `contains(Object item)` | `deque.contains(item)` |

### Iteration (Core Fix)

#### `iterator()`

Returns LIFO order (top-to-bottom), matching C# `Stack<T>.GetEnumerator()`:

```java
@Override
public Iterator<T> iterator() {
    return deque.descendingIterator();
}
```

`ArrayDeque.descendingIterator()` traverses from tail (top of stack) to head (bottom of stack), which is exactly C#'s LIFO iteration order.

This ensures:
- `for (T item : cSharpStack)` → LIFO order ✅
- `cSharpStack.stream()` → LIFO order ✅ (via `spliterator()`)

#### `spliterator()`

Overrides to ensure `stream()` also uses LIFO order:

```java
@Override
public Spliterator<T> spliterator() {
    return Spliterators.spliteratorUnknownSize(iterator(), Spliterator.ORDERED);
}
```

### ToArray / CopyTo

| C# Method | Java Method | Implementation |
|---|---|---|
| `ToArray()` | `toArray()` | Returns LIFO-ordered `Object[]`. Implementation: copy via `iterator()` into `ArrayList`, then `toArray()`. |
| `ToArray()` | `toArray(T[] a)` | Returns LIFO-ordered typed array. Same approach with `toArray(T[])`. |
| `CopyTo(T[] array, int arrayIndex)` | `copyTo(T[] array, int arrayIndex)` | Iterates LIFO and copies into `array` starting at `arrayIndex`. Throws `IndexOutOfBoundsException` on overflow. |

### TryPeek / TryPop

| C# Method | Java Method | Implementation |
|---|---|---|
| `TryPeek(out T result)` | `tryPeek(ObjectHolder<T> holder)` | If non-empty: `holder.value = deque.peekLast()`, return `true`. Else return `false`. |
| `TryPop(out T result)` | `tryPop(ObjectHolder<T> holder)` | If non-empty: `holder.value = deque.removeLast()`, return `true`. Else return `false`. |

Uses `ObjectHolder<T>` (already in compat library) to simulate C# `out` parameters.

### TrimExcess

| C# Method | Java Method | Implementation |
|---|---|---|
| `TrimExcess()` | `trimExcess()` | `ArrayDeque` has no `trimToSize()`. Implementation: `deque = new ArrayDeque<>(deque)` (copy into smaller backing array). Uses a temporary copy to avoid concurrent modification. |
| `TrimExcess(int capacity)` | `trimExcess(int capacity)` | Same approach, with `new ArrayDeque<>(capacity)` as target capacity. |

### GetEnumerator (CSharpEnumerator Interop)

| C# Method | Java Method | Implementation |
|---|---|---|
| `GetEnumerator()` | `getEnumerator()` | Returns `CSharpEnumerator.from(iterator())`, which wraps the LIFO iterator. |

This ensures explicit `GetEnumerator()` calls (used in C# enumerator patterns) also produce LIFO order.

### Equals / GetHashCode

| C# Method | Java Method | Implementation |
|---|---|---|
| `Equals(object)` | `equals(Object)` | Compares LIFO-ordered elements sequentially. Two stacks are equal if they have the same size and same elements in the same LIFO order. |
| `GetHashCode()` | `hashCode()` | Computes hash based on LIFO-ordered elements using `Objects.hash()` pattern. |

### isEmpty (Java Convenience)

- `isEmpty()` — returns `deque.isEmpty()`. Not in C# API but idiomatic Java convenience.

## TypeMappings.json Changes

### Type mapping (replace existing)

```json
{
    "csharp": "System.Collections.Generic.Stack`1",
    "java": "CSharpStack",
    "imports": ["io.github.ningpp.compat.CSharpStack"]
}
```

### Method mappings (existing mappings remain valid)

```json
{ "type": "System.Collections.Generic.Stack`1", "method": "Push", "javaMethod": "push" },
{ "type": "System.Collections.Generic.Stack`1", "method": "Pop", "javaMethod": "pop" },
{ "type": "System.Collections.Generic.Stack`1", "method": "Peek", "javaMethod": "peek" },
{ "type": "System.Collections.Generic.Stack`1", "method": "Count", "javaMethod": "size" }
```

### New method mappings to add

```json
{ "type": "System.Collections.Generic.Stack`1", "method": "Contains", "javaMethod": "contains" },
{ "type": "System.Collections.Generic.Stack`1", "method": "Clear", "javaMethod": "clear" },
{ "type": "System.Collections.Generic.Stack`1", "method": "ToArray", "javaMethod": "toArray" },
{ "type": "System.Collections.Generic.Stack`1", "method": "CopyTo", "javaMethod": "copyTo" },
{ "type": "System.Collections.Generic.Stack`1", "method": "TryPeek", "javaMethod": "tryPeek" },
{ "type": "System.Collections.Generic.Stack`1", "method": "TryPop", "javaMethod": "tryPop" },
{ "type": "System.Collections.Generic.Stack`1", "method": "TrimExcess", "javaMethod": "trimExcess" },
{ "type": "System.Collections.Generic.Stack`1", "method": "GetEnumerator", "javaMethod": "getEnumerator" }
```

Note: `GetEnumerator` mapping ensures the converter emits `getEnumerator()` instead of the default `iterator()`, which would otherwise bypass CSharpStack's LIFO iterator.

## Converter-Side Changes

### GetEnumerator handling in InvocationExpressionTransformer.cs

Currently `BuildIteratorExpressionForExplicitGetEnumerator` falls back to `{receiver}.iterator()` for non-array types. For `CSharpStack`, the `GetEnumerator` → `getEnumerator` method mapping will be resolved by the converter's normal method mapping pipeline, so no special-case code is needed in the converter.

### Foreach handling

`foreach (var item in stack)` translates to `for (var item : cSharpStack)`, which uses `CSharpStack.iterator()` (LIFO). No converter change needed.

### LINQ Reverse handling

`stack.Reverse()` (LINQ) translates to `Collections.reverse(...)` on the LIFO-ordered stream. This reverses LIFO → FIFO, which matches C# behavior (C# Reverse() on LIFO produces FIFO). No converter change needed.

## Test Plan

`CSharpStackTest.java` in `src/test/java/io/github/ningpp/compat/`:

1. Default constructor creates empty stack
2. Constructor with capacity creates empty stack
3. Constructor with collection pushes elements in order (last element on top)
4. `push` / `pop` / `peek` basic operations
5. `pop` and `peek` on empty stack throw `NoSuchElementException`
6. `size()` and `isEmpty()` reflect state correctly
7. `contains()` finds pushed elements
8. `clear()` empties the stack
9. **`iterator()` returns LIFO order** — push A, B, C; iterator yields C, B, A
10. **`stream()` returns LIFO order** — same test with `stream().collect(toList())`
11. **`toArray()` returns LIFO-ordered array** — push A, B, C; array = [C, B, A]
12. **`toArray(T[])` returns LIFO-ordered typed array**
13. `copyTo()` copies LIFO-ordered elements to array at given index
14. `tryPeek()` succeeds on non-empty, fails on empty
15. `tryPop()` succeeds on non-empty, fails on empty; modifies stack
16. `trimExcess()` does not lose elements
17. `getEnumerator()` returns `CSharpEnumerator` with LIFO order
18. `equals()` compares LIFO-ordered elements
19. `hashCode()` consistent with equals

## Files to Create/Modify

| Action | File |
|---|---|
| Create | `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpStack.java` |
| Create | `java/csharptojava-compat/src/test/java/io/github/ningpp/compat/CSharpStackTest.java` |
| Modify | `config/TypeMappings.json` — type mapping + new method mappings |
