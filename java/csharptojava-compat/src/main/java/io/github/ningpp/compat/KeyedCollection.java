package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public abstract class KeyedCollection<TKey, TItem> implements Iterable<TItem> {
    private final List<TItem> items = new ArrayList<>();
    private final Map<TKey, TItem> byKey = new LinkedHashMap<>();

    protected abstract TKey getKeyForItem(TItem item);

    public boolean add(TItem item) {
        TKey key = getKeyForItem(item);
        if (byKey.containsKey(key)) {
            throw new IllegalArgumentException("An item with the same key has already been added.");
        }

        items.add(item);
        byKey.put(key, item);
        return true;
    }

    public boolean contains(TKey key) {
        return byKey.containsKey(key);
    }

    public boolean containsItem(TItem item) {
        return items.contains(item);
    }

    public TItem get(TKey key) {
        return byKey.get(key);
    }

    public TItem get(int index) {
        return items.get(index);
    }

    public boolean remove(TItem item) {
        if (!items.remove(item)) {
            return false;
        }

        byKey.remove(getKeyForItem(item));
        return true;
    }

    public TItem removeAt(int index) {
        TItem removed = items.remove(index);
        byKey.remove(getKeyForItem(removed));
        return removed;
    }

    public void clear() {
        items.clear();
        byKey.clear();
    }

    public int size() {
        return items.size();
    }

    public int getCount() {
        return size();
    }

    @Override
    public Iterator<TItem> iterator() {
        return items.iterator();
    }
}
