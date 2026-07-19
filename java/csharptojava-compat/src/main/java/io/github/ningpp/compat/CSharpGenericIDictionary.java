package io.github.ningpp.compat;

import java.util.*;
import java.util.Map.Entry;

public interface CSharpGenericIDictionary<K, V> extends CSharpICollection<CSharpKeyValuePair<K, V>> {
    void add(K key, V value);
    boolean containsKey(Object key);
    boolean containsValue(Object value);
    boolean remove(Object key);
    V get(K key);
    V put(K key, V value);
    CSharpICollection<K> getKeys();
    CSharpICollection<V> getValues();
    V tryGetValue(K key);
    boolean getIsReadOnly();
    int getCount();

    default Set<K> keySet() {
        Set<K> keys = new LinkedHashSet<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            keys.add(pair.getKey());
        }
        return keys;
    }

    default Collection<V> values() {
        List<V> vals = new ArrayList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            vals.add(pair.getValue());
        }
        return vals;
    }

    default Set<Entry<K, V>> entrySet() {
        Set<Entry<K, V>> entries = new LinkedHashSet<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            entries.add(pair);
        }
        return entries;
    }

    default V getOrDefault(K key, V defaultValue) {
        V v = get(key);
        return v != null || containsKey(key) ? v : defaultValue;
    }
}
