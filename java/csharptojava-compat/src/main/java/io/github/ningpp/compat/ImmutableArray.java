package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Compat for System.Collections.Immutable.ImmutableArray&lt;T&gt; — minimal
 * immutable array wrapper used by the converted tests.
 */
public final class ImmutableArray<T> implements Iterable<T> {
    private final List<T> items;
    private final boolean isDefault;

    public ImmutableArray() {
        this.items = new ArrayList<>();
        this.isDefault = true;
    }

    private ImmutableArray(List<T> items) {
        this.items = items;
        this.isDefault = false;
    }

    public static <T> ImmutableArray<T> createRange(CSharpGenericIterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableArray<>(list);
    }

    public static <T> ImmutableArray<T> createRange(Iterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableArray<>(list);
    }

    @SafeVarargs
    public static <T> ImmutableArray<T> create(T... items) {
        List<T> list = new ArrayList<>();
        for (T item : items) {
            list.add(item);
        }
        return new ImmutableArray<>(list);
    }

    public static <T> ImmutableArray<T> toImmutableArray(CSharpList<T> source) {
        List<T> list = new ArrayList<>();
        list.addAll(source);
        return new ImmutableArray<>(list);
    }

    public static <T> ImmutableArray<T> toImmutableArray(CSharpGenericIterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableArray<>(list);
    }

    public static <T> ImmutableArray<T> toImmutableArray(Iterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableArray<>(list);
    }

    public boolean getIsDefault() {
        return isDefault;
    }

    public boolean getIsDefaultOrEmpty() {
        return isDefault || items.isEmpty();
    }

    public int getLength() {
        return items.size();
    }

    public int size() {
        return items.size();
    }

    public T get(int index) {
        return items.get(index);
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public List<T> toList() {
        return new ArrayList<>(items);
    }

    @Override
    public java.util.Iterator<T> iterator() {
        return items.iterator();
    }

    public java.util.stream.Stream<T> stream() {
        return items.stream();
    }

    public java.util.Spliterator<T> spliterator() {
        return items.spliterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
