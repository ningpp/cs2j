package io.github.ningpp.compat;

import java.util.concurrent.ConcurrentHashMap;

/**
 * C# ConcurrentDictionary&lt;K,V&gt; compat view. Only the members emitted by the
 * converter are provided; operations delegate to ConcurrentHashMap (the striped
 * locking difference is irrelevant to the generated call sites).
 */
public final class CSharpConcurrentDictionary<K, V> {
    private final ConcurrentHashMap<K, V> map;

    public CSharpConcurrentDictionary() {
        this.map = new ConcurrentHashMap<>();
    }

    public CSharpConcurrentDictionary(int concurrencyLevel, int capacity) {
        this.map = new ConcurrentHashMap<>(Math.max(1, capacity));
    }

    public CSharpConcurrentDictionary(CSharpGenericIDictionary<K, V> dictionary) {
        this.map = new ConcurrentHashMap<>();
        if (dictionary != null) {
            for (CSharpKeyValuePair<K, V> pair : dictionary) {
                this.map.put(pair.getKey(), pair.getValue());
            }
        }
    }

    public boolean tryAdd(K key, V value) {
        return map.putIfAbsent(key, value) == null;
    }

    public boolean tryRemove(K key, ObjectHolder<V> result) {
        V value = map.remove(key);
        result.value = value;
        return value != null;
    }

    public boolean tryGetValue(K key, ObjectHolder<V> result) {
        V value = map.get(key);
        result.value = value;
        return value != null;
    }

    public V get(K key) {
        return map.get(key);
    }

    public boolean containsKey(K key) {
        return map.containsKey(key);
    }

    public void put(K key, V value) {
        map.put(key, value);
    }

    public int getCount() {
        return map.size();
    }

    public void clear() {
        map.clear();
    }

    public int size() {
        return map.size();
    }
}
