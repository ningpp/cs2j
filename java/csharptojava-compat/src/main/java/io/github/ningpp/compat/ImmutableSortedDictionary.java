package io.github.ningpp.compat;

import java.util.TreeMap;
import java.util.TreeSet;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.ImmutableSortedDictionary&lt;K,V&gt;.
 */
public class ImmutableSortedDictionary<K, V> implements IImmutableDictionary<K, V> {
    private final TreeMap<K, V> items;

    public ImmutableSortedDictionary() {
        this.items = new TreeMap<>();
    }

    private ImmutableSortedDictionary(TreeMap<K, V> items) {
        this.items = items;
    }

    public static <K, V> ImmutableSortedDictionary<K, V> createRange(Iterable<Map.Entry<K, V>> source) {
        TreeMap<K, V> map = new TreeMap<>();
        for (Map.Entry<K, V> entry : source) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableSortedDictionary<>(map);
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

    public boolean containsKey(K key) {
        return items.containsKey(key);
    }

    public V get(K key) {
        return items.get(key);
    }

    public Set<K> keySet() {
        return items.keySet();
    }

    public Set<Map.Entry<K, V>> entrySet() {
        return items.entrySet();
    }

    @Override
    public java.util.Iterator<Map.Entry<K, V>> iterator() {
        return items.entrySet().iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
