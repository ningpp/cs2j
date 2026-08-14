package io.github.ningpp.compat;

import java.util.concurrent.ConcurrentHashMap;

public class ConcurrentHashMapHelper {
    private ConcurrentHashMapHelper() {
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    public static boolean tryAdd(ConcurrentHashMap map, Object key, Object value) {
        return map.putIfAbsent(key, value) == null;
    }

    /** Mirrors ConcurrentDictionary.GetOrAdd(key, valueFactory) — existing value or the new one. */
    public static <K, V> V getOrAdd(ConcurrentHashMap<K, V> map, K key, V value) {
        return map.computeIfAbsent(key, k -> value);
    }

    /** Mirrors ConcurrentDictionary.GetOrAdd(key, Func) — existing value or factory result. */
    public static <K, V> V getOrAdd(ConcurrentHashMap<K, V> map, K key, java.util.function.Function<K, V> valueFactory) {
        return map.computeIfAbsent(key, valueFactory);
    }

    /** Mirrors ConcurrentDictionary(IDictionary<K,V>) — copies entries into a new map. */
    public static <K, V> ConcurrentHashMap<K, V> create(CSharpGenericIDictionary<K, V> dictionary) {
        ConcurrentHashMap<K, V> map = new ConcurrentHashMap<>();
        for (CSharpKeyValuePair<K, V> pair : dictionary) {
            map.put(pair.getKey(), pair.getValue());
        }
        return map;
    }
}
