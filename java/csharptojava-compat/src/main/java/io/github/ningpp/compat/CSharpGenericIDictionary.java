package io.github.ningpp.compat;

import java.util.*;
import java.util.Map.Entry;

public interface CSharpGenericIDictionary<K, V> extends CSharpICollection<CSharpKeyValuePair<K, V>> {
    void add(K key, V value);
    /** Default implementation so converter-generated classes that emit containsKey(K)
     * (erasing to a different signature) still compile. */
    default boolean containsKey(Object key) {
        for (CSharpKeyValuePair<K, V> pair : this) {
            if (java.util.Objects.equals(pair.getKey(), key)) {
                return true;
            }
        }
        return false;
    }
    /** Default implementation so converter-generated classes that don't emit
     * containsValue still compile. */
    default boolean containsValue(Object value) {
        for (CSharpKeyValuePair<K, V> pair : this) {
            if (java.util.Objects.equals(pair.getValue(), value)) {
                return true;
            }
        }
        return false;
    }
    /** Default implementation: converter-generated classes emit remove(TKey) which
     * erases to a different signature; the default keeps them compilable. */
    default boolean remove(Object key) {
        return false;
    }
    V get(K key);
    V put(K key, V value);
    /** Default implementations so converter-generated classes returning Set/Collection
     * from keySet()/values() (via the compat adapter) still compile. */
    default CSharpICollection<K> getKeys() {
        CSharpList<K> list = new CSharpList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            list.add(pair.getKey());
        }
        return list;
    }
    default CSharpICollection<V> getValues() {
        CSharpList<V> list = new CSharpList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            list.add(pair.getValue());
        }
        return list;
    }
    /** Default implementation so converter-generated classes that emit the
     * out-parameter shape (tryGetValue(K, ObjectHolder&lt;V&gt;)) still compile. */
    default V tryGetValue(K key) {
        return get(key);
    }
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
