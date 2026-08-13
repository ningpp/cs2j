package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Compat for System.Collections.ObjectModel.ObservableCollection&lt;T&gt;.
 */
public class ObservableCollection<T> implements Iterable<T> {
    private final List<T> items;

    public ObservableCollection() {
        this.items = new ArrayList<>();
    }

    public ObservableCollection(Collection<T> source) {
        this.items = new ArrayList<>(source);
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

    public void add(T item) {
        items.add(item);
    }

    public boolean remove(T item) {
        return items.remove(item);
    }

    public void clear() {
        items.clear();
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
