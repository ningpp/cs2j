package io.github.ningpp.compat;

import java.util.*;
import java.util.Map.Entry;

public interface CSharpGenericIDictionary<K, V> extends CSharpIterable<CSharpKeyValuePair<K, V>> {
    void add(K key, V value);
    /** Default implementation so converter-generated classes that emit containsKey(K)
     * (erasing to a different signature) still compile. */
    default boolean containsKey(K key) {
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
    default boolean remove(K key) {
        return false;
    }
    V get(K key);

    /**
     * Mirrors Dictionary&lt;TKey, TValue&gt;.get_Item: throws KeyNotFoundException
     * when the key is absent. Emitted for C# indexer reads (dict[key]).
     */
    default V getChecked(K key) {
        if (!containsKey(key)) {
            throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
        }
        return get(key);
    }    V put(K key, V value);
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
    /** One-argument convenience shape (returns the value or null). */
    default V tryGetValue(K key) {
        return get(key);
    }
    /** Out-parameter shape emitted by the ref/out lowering for
     * <c>Dictionary.TryGetValue(key, out value)</c>. */
    default boolean tryGetValue(K key, ObjectHolder<V> result) {
        if (containsKey(key)) {
            result.value = get(key);
            return true;
        }
        result.value = null;
        return false;
    }
    boolean getIsReadOnly();
    int getCount();

    default void clear() {
    }

    default boolean getIsFixedSize() {
        return false;
    }

    default boolean getIsSynchronized() {
        return false;
    }

    default Object getSyncRoot() {
        return this;
    }

    default void copyTo(CSharpArray array, int index) {
    }

    default void copyTo(CSharpKeyValuePair<K, V>[] array, int index) {
    }

    default Object[] toArray() {
        java.util.List<CSharpKeyValuePair<K, V>> list = new java.util.ArrayList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            list.add(pair);
        }
        return list.toArray();
    }

    default int size() {
        return getCount();
    }

    default boolean isEmpty() {
        return getCount() == 0;
    }

    /** KeyValuePair-shaped members that were previously inherited from the
     * CSharpICollection chain. */
    default boolean add(CSharpKeyValuePair<K, V> item) {
        add(item.getKey(), item.getValue());
        return true;
    }

    default boolean remove(CSharpKeyValuePair<K, V> item) {
        return remove(item.getKey());
    }

    default boolean contains(CSharpKeyValuePair<K, V> item) {
        return containsKey(item.getKey());
    }

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
        return containsKey(key) ? get(key) : defaultValue;
    }
}
