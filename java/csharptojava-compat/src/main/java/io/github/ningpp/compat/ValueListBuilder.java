package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/**
 * Java equivalent of C# System.Collections.Generic.ValueListBuilder&lt;T&gt;.
 * Backed by an ArrayList for simplicity (the C# version uses stack-allocated
 * Span&lt;T&gt; for small sizes, but Java has no equivalent of ref structs).
 *
 * <p>Public API mirrors the C# ValueListBuilder&lt;T&gt; ref struct:
 * <ul>
 *   <li>Append(T) → {@link #append(Object)}</li>
 *   <li>Pop() → {@link #pop()}</li>
 *   <li>Insert(int, ReadOnlySpan) → {@link #insert(int, Object)}</li>
 *   <li>Length (get/set) → {@link #getLength()} / {@link #setLength(int)}</li>
 *   <li>this[int] → {@link #get(int)} / {@link #set(int, Object)}</li>
 *   <li>Clear() → {@link #clear()}</li>
 *   <li>ToArray() → {@link #toArray()}</li>
 *   <li>AsSpan() → {@link #asList()}</li>
 * </ul>
 */
public class ValueListBuilder<T> {
    private final List<T> list;

    public ValueListBuilder() {
        this.list = new ArrayList<>();
    }

    public ValueListBuilder(int initialCapacity) {
        this.list = new ArrayList<>(initialCapacity);
    }

    /**
     * Appends an item.  C# Append(T) → Java append(T) via camelCase conversion.
     */
    public void append(T item) {
        list.add(item);
    }

    /**
     * Removes and returns the last element.
     * C# Pop() → Java pop() via camelCase conversion.
     *
     * @throws IndexOutOfBoundsException if the list is empty
     */
    public T pop() {
        return list.remove(list.size() - 1);
    }

    /**
     * Inserts an item at the specified index.
     * C# Insert(int, ReadOnlySpan&lt;T&gt;) → Java insert(int, T).
     */
    public void insert(int index, T item) {
        list.add(index, item);
    }

    /**
     * Returns the number of elements.  C# Length property → Java getLength()
     * via the getter pattern.
     */
    public int getLength() {
        return list.size();
    }

    /**
     * Sets the number of elements.  C# Length property setter → Java setLength(int).
     * If newLength is smaller, truncates the list.  If larger, pads with nulls.
     */
    public void setLength(int newLength) {
        int current = list.size();
        if (newLength < current) {
            list.subList(newLength, current).clear();
        } else if (newLength > current) {
            for (int i = current; i < newLength; i++) {
                list.add(null);
            }
        }
    }

    /**
     * Returns true if the list has no elements.
     */
    public boolean isEmpty() {
        return list.isEmpty();
    }

    /**
     * Indexer access.  C# this[int index] → Java get(int index).
     */
    public T get(int index) {
        return list.get(index);
    }

    /**
     * Indexer setter.  C# this[int index] = value → Java set(int index, T value).
     */
    public T set(int index, T value) {
        return list.set(index, value);
    }

    /**
     * Removes the element at the specified index.
     * C# RemoveAt(int) → Java removeAt(int) via camelCase conversion.
     */
    public T removeAt(int index) {
        return list.remove(index);
    }

    /**
     * Returns true if the list contains the specified element.
     * C# Contains(T) → Java contains(T) via camelCase conversion.
     */
    public boolean contains(T item) {
        return list.contains(item);
    }

    /**
     * Returns the index of the first occurrence of the specified element, or -1.
     * C# IndexOf(T) → Java indexOf(T) via camelCase conversion.
     */
    public int indexOf(T item) {
        return list.indexOf(item);
    }

    /**
     * C# Clear() → Java clear() via camelCase conversion.
     */
    public void clear() {
        list.clear();
    }

    /**
     * C# ToArray() → Java toArray() via camelCase conversion.
     */
    @SuppressWarnings("unchecked")
    public T[] toArray() {
        return (T[]) list.toArray();
    }

    /**
     * Returns the underlying list (for interop with Java collection APIs).
     * Also serves as the Java equivalent of C# AsSpan().
     */
    public List<T> asList() {
        return list;
    }

    @Override
    public String toString() {
        return list.toString();
    }
}
