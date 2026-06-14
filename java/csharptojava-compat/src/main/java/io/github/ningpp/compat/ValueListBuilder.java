package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/**
 * Java equivalent of C# System.Collections.Generic.ValueListBuilder&lt;T&gt;.
 * Backed by an ArrayList for simplicity (the C# version uses stack-allocated
 * Span&lt;T&gt; for small sizes, but Java has no equivalent of ref structs).
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
     * Returns the number of elements.  C# Length property → Java getLength()
     * via the getter pattern.
     */
    public int getLength() {
        return list.size();
    }

    /**
     * Indexer access.  C# this[int index] → Java get(int index).
     */
    public T get(int index) {
        return list.get(index);
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
     */
    public List<T> asList() {
        return list;
    }

    @Override
    public String toString() {
        return list.toString();
    }
}
