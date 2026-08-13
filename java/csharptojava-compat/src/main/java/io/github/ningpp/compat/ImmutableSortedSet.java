package io.github.ningpp.compat;

import java.util.TreeSet;

/**
 * Compat for System.Collections.Immutable.ImmutableSortedSet&lt;T&gt;.
 */
public class ImmutableSortedSet<T> implements IImmutableSet<T> {
    private final TreeSet<T> items;

    public ImmutableSortedSet() {
        this.items = new TreeSet<>();
    }

    private ImmutableSortedSet(TreeSet<T> items) {
        this.items = items;
    }

    public static <T> ImmutableSortedSet<T> createRange(Iterable<T> source) {
        TreeSet<T> set = new TreeSet<>();
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableSortedSet<>(set);
    }

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean contains(T item) {
        return items.contains(item);
    }

    @Override
    public java.util.Iterator<T> iterator() {
        return items.iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
