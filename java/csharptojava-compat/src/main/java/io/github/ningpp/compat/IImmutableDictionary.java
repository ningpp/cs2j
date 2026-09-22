package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.IImmutableDictionary&lt;K,V&gt;.
 * Intentionally NOT Iterable — converter-generated wrappers implement
 * Iterable&lt;CSharpKeyValuePair&lt;K,V&gt;&gt; which clashes with Map.Entry iteration.
 */
public interface IImmutableDictionary<K, V> {
    int size();

    boolean isEmpty();

    boolean containsKey(K key);

    V get(K key);

    CSharpGenericIterable<K> getKeys();

    CSharpGenericIterable<V> getValues();

    default CSharpGenericIterable<K> keySet() {
        return getKeys();
    }

    default CSharpGenericIterable<V> values() {
        return getValues();
    }

    default Set<Map.Entry<K, V>> entrySet() {
        Set<Map.Entry<K, V>> entries = new LinkedHashSet<>();
        for (K key : getKeys()) {
            entries.add(new java.util.AbstractMap.SimpleEntry<K, V>(key, get(key)));
        }
        return entries;
    }

    default IImmutableDictionary<K, V> add(K key, V value) {
        return this;
    }

    default IImmutableDictionary<K, V> addRange(CSharpGenericIterable<CSharpKeyValuePair<K, V>> pairs) {
        for (CSharpKeyValuePair<K, V> pair : pairs) {
            add(pair.getKey(), pair.getValue());
        }
        return this;
    }

    // No fluent clear(): the classes realize CSharpReadOnlyDict, whose CSharpGenericIterable
    // ancestor declares void clear() — a fluent clear() would clash on erasure. Call sites
    // route to the static helper below instead.

    /**
     * Stand-in for C# IImmutableDictionary&lt;K,V&gt;.Clear() — an empty dictionary. A fluent
     * instance clear() cannot live on this interface: implementations also realize
     * CSharpReadOnlyDict, whose Collection ancestor declares void clear().
     */
    static <K, V> IImmutableDictionary<K, V> clear(IImmutableDictionary<K, V> dict) {
        return new ImmutableDictionary<>();
    }

    default CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> iterator() {
        CSharpList<CSharpKeyValuePair<K, V>> list = new CSharpList<>();
        for (K key : keySet()) {
            list.add(new CSharpKeyValuePair<>(key, get(key)));
        }
        return CSharpGenericEnumerator.from(list.iterator());
    }

    default IImmutableDictionary<K, V> removeCSharp(K key) {
        return this;
    }

    default IImmutableDictionary<K, V> removeRange(CSharpGenericIterable<K> keys) {
        for (K key : keys) {
            removeCSharp(key);
        }
        return this;
    }

    default IImmutableDictionary<K, V> setItem(K key, V value) {
        return this;
    }

    default IImmutableDictionary<K, V> setItems(CSharpGenericIterable<CSharpKeyValuePair<K, V>> items) {
        for (CSharpKeyValuePair<K, V> pair : items) {
            setItem(pair.getKey(), pair.getValue());
        }
        return this;
    }

    default boolean tryGetKey(K equalKey, ObjectHolder<K> actualKey) {
        if (containsKey(equalKey)) {
            actualKey.value = equalKey;
            return true;
        }
        return false;
    }

    default V tryGetValue(K key) {
        return get(key);
    }

    default boolean contains(CSharpKeyValuePair<K, V> pair) {
        return containsKey(pair.getKey());
    }
}
