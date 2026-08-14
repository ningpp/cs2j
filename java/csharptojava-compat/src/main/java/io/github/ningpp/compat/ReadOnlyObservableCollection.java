package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Compat for System.Collections.ObjectModel.ReadOnlyObservableCollection&lt;T&gt;.
 */
public class ReadOnlyObservableCollection<T> implements Iterable<T> {
    private final List<T> items;

    public ReadOnlyObservableCollection(Collection<T> source) {
        this.items = new ArrayList<>(source);
    }

    public ReadOnlyObservableCollection(ObservableCollection<T> source) {
        List<T> copied = new ArrayList<>();
        for (T item : source) {
            copied.add(item);
        }
        this.items = copied;
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
