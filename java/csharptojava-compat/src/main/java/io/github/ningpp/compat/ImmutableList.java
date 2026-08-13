package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Compat for System.Collections.Immutable.ImmutableList&lt;T&gt; — minimal
 * immutable list wrapper used by the converted tests.
 */
public class ImmutableList<T> implements IImmutableList<T> {
    private final List<T> items;

    public ImmutableList() {
        this.items = new ArrayList<>();
    }

    private ImmutableList(List<T> items) {
        this.items = items;
    }

    public static <T> ImmutableList<T> createRange(CSharpGenericIterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableList<>(list);
    }

    public static <T> ImmutableList<T> createRange(Iterable<T> source) {
        List<T> list = new ArrayList<>();
        for (T item : source) {
            list.add(item);
        }
        return new ImmutableList<>(list);
    }

    public static <T> ImmutableList<T> createRange(Collection<T> source) {
        return new ImmutableList<>(new ArrayList<>(source));
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

    public T get(int index) {
        return items.get(index);
    }

    public List<T> toList() {
        return new ArrayList<>(items);
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
