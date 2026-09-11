package io.github.ningpp.compat;

import java.util.concurrent.ConcurrentHashMap;

public class ConcurrentHashMapHelper {
    private ConcurrentHashMapHelper() {
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    public static boolean tryAdd(ConcurrentHashMap map, Object key, Object value) {
        // Route the key through TypeHelper.converterCacheKey: for a raw class with
        // published member type arguments (JsonSerializerOptions._converters) the entry
        // is stored under the closed generic type instead of the erased class, so the
        // generated raw-class lookup cannot hand a converter built for one member's type
        // arguments to a different member. Non-class keys and classes without published
        // arguments are unaffected.
        Object effectiveKey = TypeHelper.converterCacheKey(key);
        return map.putIfAbsent(effectiveKey, value) == null;
    }

    /** Mirrors ConcurrentDictionary.GetOrAdd(key, valueFactory) — existing value or the new one. */
    @SuppressWarnings({"unchecked", "rawtypes"})
    public static <K, V> V getOrAdd(ConcurrentHashMap<K, V> map, K key, V value) {
        // Same reasoning as tryAdd: the generated JsonClassInfo cache
        // (JsonSerializerOptions._classes) is keyed by the erased class, so
        // CSharpList<String> and CSharpList<Integer> would share one JsonClassInfo —
        // and therefore one element/key type — whenever both are cached. Routing the
        // key through converterCacheKey keeps them apart when member type arguments
        // are published; classes without published arguments keep sharing one entry.
        Object effectiveKey = TypeHelper.converterCacheKey(key);
        return (V) map.computeIfAbsent((K) effectiveKey, k -> value);
    }

    /** Mirrors ConcurrentDictionary.GetOrAdd(key, Func) — existing value or factory result. */
    @SuppressWarnings({"unchecked", "rawtypes"})
    public static <K, V> V getOrAdd(ConcurrentHashMap<K, V> map, K key, java.util.function.Function<K, V> valueFactory) {
        Object effectiveKey = TypeHelper.converterCacheKey(key);
        // The factory still receives the caller's key, not the closed-generic cache key.
        return (V) map.computeIfAbsent((K) effectiveKey, k -> valueFactory.apply(key));
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
