package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.List;

/** Compat for System.Collections.Concurrent.ConcurrentBag&lt;T&gt;. */
public class ConcurrentBag<T> implements CSharpGenericIterable<T> {
    private final List<T> items = new ArrayList<>();

    public ConcurrentBag() {
    }

    public ConcurrentBag(java.util.Collection<? extends T> source) {
        if (source != null) {
            items.addAll(source);
        }
    }

    @Override
    public boolean add(T item) {
        return items.add(item);
    }

    public boolean tryAdd(T item) {
        items.add(item);
        return true;
    }

    public int getCount() {
        return items.size();
    }

    @Override
    public int size() {
        return items.size();
    }

    @Override
    public boolean isEmpty() {
        return items.isEmpty();
    }

    @Override
    public boolean contains(Object item) {
        return items.contains(item);
    }

    public Object[] toArray() {
        return items.toArray();
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(items.iterator());
    }
}
