package io.github.ningpp.compat;

import java.util.LinkedHashSet;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.ImmutableHashSet&lt;T&gt;.
 */
public class ImmutableHashSet<T> implements IImmutableSet<T> {
    private final Set<T> items;

    public ImmutableHashSet() {
        this.items = new LinkedHashSet<>();
    }

    private ImmutableHashSet(Set<T> items) {
        this.items = items;
    }

    public static <T> ImmutableHashSet<T> createRange(Iterable<T> source) {
        Set<T> set = new LinkedHashSet<>();
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableHashSet<>(set);
    }

    public static <T> ImmutableHashSet<T> createRange(CSharpGenericIterable<T> source) {
        Set<T> set = new LinkedHashSet<>();
        for (T item : source) {
            set.add(item);
        }
        return new ImmutableHashSet<>(set);
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

    public Set<T> toSet() {
        return new LinkedHashSet<>(items);
    }

    public java.util.stream.Stream<T> stream() {
        return items.stream();
    }

    public java.util.Spliterator<T> spliterator() {
        return items.spliterator();
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
