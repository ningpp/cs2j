package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.ImmutableDictionary&lt;K,V&gt; — minimal
 * immutable dictionary wrapper.
 */
public class ImmutableDictionary<K, V> implements IImmutableDictionary<K, V> {
    private final Map<K, V> items;

    public ImmutableDictionary() {
        this.items = new LinkedHashMap<>();
    }

    private ImmutableDictionary(Map<K, V> items) {
        this.items = items;
    }

    public static <K, V> ImmutableDictionary<K, V> createRange(Iterable<Map.Entry<K, V>> source) {
        Map<K, V> map = new LinkedHashMap<>();
        for (Map.Entry<K, V> entry : source) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableDictionary<>(map);
    }

    public static <K, V> ImmutableDictionary<K, V> createRange(CSharpGenericIterable<Map.Entry<K, V>> source) {
        Map<K, V> map = new LinkedHashMap<>();
        for (Map.Entry<K, V> entry : source) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableDictionary<>(map);
    }

    public static <K, V> ImmutableDictionary<K, V> createRange(CSharpGenericIDictionary<K, V> source) {
        Map<K, V> map = new LinkedHashMap<>();
        for (Map.Entry<K, V> entry : source.entrySet()) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableDictionary<>(map);
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

    public Map<K, V> toMap() {
        return new LinkedHashMap<>(items);
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
