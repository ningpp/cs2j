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

    /** C# ImmutableDictionary.Empty — the empty singleton. */
    @SuppressWarnings({"rawtypes", "unchecked"})
    public static final ImmutableDictionary Empty = new ImmutableDictionary();

    public ImmutableDictionary() {
        this.items = new LinkedHashMap<>();
    }

    /** C# ImmutableDictionary.Create<K,V>() — an empty dictionary. */
    public static <K, V> ImmutableDictionary<K, V> create() {
        return new ImmutableDictionary<>();
    }

    /** C# ImmutableDictionary<K,V>.Add(key, value) — returns a new dictionary. */
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

    /**
     * C#-named CreateRange used by the JSON immutable-dictionary factory, which
     * looks up a static generic method literally named "CreateRange" taking one
     * IEnumerable to build the deserialized instance.
     */
    public static <K, V> ImmutableDictionary<K, V> CreateRange(CSharpGenericIterable<Map.Entry<K, V>> source) {
        return createRange(source);
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

    public CSharpGenericIterable<K> keySet() {
        return CSharpGenericIterable.from(items.keySet());
    }

    public CSharpGenericIterable<K> getKeys() {
        return CSharpGenericIterable.from(items.keySet());
    }

    public CSharpGenericIterable<V> values() {
        return CSharpGenericIterable.from(items.values());
    }

    public CSharpGenericIterable<V> getValues() {
        return CSharpGenericIterable.from(items.values());
    }

    public Set<Map.Entry<K, V>> entrySet() {
        return items.entrySet();
    }

    public Map<K, V> toMap() {
        return new LinkedHashMap<>(items);
    }

    @Override
    public CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> iterator() {
        CSharpList<CSharpKeyValuePair<K, V>> list = new CSharpList<>();
        for (Map.Entry<K, V> entry : items.entrySet()) {
            list.add(new CSharpKeyValuePair<>(entry.getKey(), entry.getValue()));
        }
        return CSharpGenericEnumerator.from(list.iterator());
    }

    public boolean contains(CSharpKeyValuePair<K, V> pair) {
        return items.containsKey(pair.getKey());
    }

    public V tryGetValue(K key) {
        return items.get(key);
    }

    public boolean tryGetKey(K equalKey, ObjectHolder<K> actualKey) {
        for (K key : items.keySet()) {
            if (java.util.Objects.equals(key, equalKey)) {
                actualKey.value = key;
                return true;
            }
        }
        return false;
    }

    @Override
    public ImmutableDictionary<K, V> add(K key, V value) {
        Map<K, V> copy = new LinkedHashMap<>(items);
        copy.put(key, value);
        return new ImmutableDictionary<>(copy);
    }

    @Override
    public ImmutableDictionary<K, V> clear() {
        return new ImmutableDictionary<>();
    }

    @Override
    public ImmutableDictionary<K, V> removeCSharp(K key) {
        Map<K, V> copy = new LinkedHashMap<>(items);
        copy.remove(key);
        return new ImmutableDictionary<>(copy);
    }

    @Override
    public ImmutableDictionary<K, V> removeRange(CSharpGenericIterable<K> keys) {
        Map<K, V> copy = new LinkedHashMap<>(items);
        for (K key : keys) {
            copy.remove(key);
        }
        return new ImmutableDictionary<>(copy);
    }

    @Override
    public ImmutableDictionary<K, V> setItem(K key, V value) {
        return add(key, value);
    }

    @Override
    public ImmutableDictionary<K, V> setItems(CSharpGenericIterable<CSharpKeyValuePair<K, V>> items) {
        Map<K, V> copy = new LinkedHashMap<>(this.items);
        for (CSharpKeyValuePair<K, V> pair : items) {
            copy.put(pair.getKey(), pair.getValue());
        }
        return new ImmutableDictionary<>(copy);
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
