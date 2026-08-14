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

    default IImmutableDictionary<K, V> clear() {
        return this;
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
