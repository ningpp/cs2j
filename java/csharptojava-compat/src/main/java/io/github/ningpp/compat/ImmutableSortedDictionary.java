package io.github.ningpp.compat;

import java.util.TreeMap;
import java.util.TreeSet;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.ImmutableSortedDictionary&lt;K,V&gt;.
 *
 * <p>Realizes {@link CSharpReadOnlyDict} (the IReadOnlyDictionary&lt;K,V&gt; port), exactly
 * like .NET's ImmutableSortedDictionary — the generated STJ converters' erased bridge
 * methods cast the untyped entry to CSharpReadOnlyDict.
 */
public class ImmutableSortedDictionary<K, V> implements IImmutableDictionary<K, V>, CSharpReadOnlyDict<K, V> {
    private final TreeMap<K, V> items;

    public ImmutableSortedDictionary() {
        this.items = new TreeMap<>();
    }

    private ImmutableSortedDictionary(TreeMap<K, V> items) {
        this.items = items;
    }

    public static <K, V> ImmutableSortedDictionary<K, V> createRange(Iterable<Map.Entry<K, V>> source) {
        TreeMap<K, V> map = new TreeMap<>();
        for (Map.Entry<K, V> entry : source) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableSortedDictionary<>(map);
    }

    public static <K, V> ImmutableSortedDictionary<K, V> createRange(CSharpGenericIDictionary<K, V> source) {
        TreeMap<K, V> map = new TreeMap<>();
        for (Map.Entry<K, V> entry : source.entrySet()) {
            map.put(entry.getKey(), entry.getValue());
        }
        return new ImmutableSortedDictionary<>(map);
    }

    /**
     * C#-named CreateRange used by the JSON immutable-dictionary factory, which
     * looks up a static generic method literally named "CreateRange" taking one
     * IEnumerable to build the deserialized instance.
     */
    public static <K, V> ImmutableSortedDictionary<K, V> CreateRange(CSharpGenericIterable<Map.Entry<K, V>> source) {
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

    public Set<Map.Entry<K, V>> entrySet() {
        return items.entrySet();
    }

    public CSharpGenericIterable<V> values() {
        return CSharpGenericIterable.from(items.values());
    }

    public CSharpGenericIterable<V> getValues() {
        return CSharpGenericIterable.from(items.values());
    }

    @Override
    public CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> iterator() {
        CSharpList<CSharpKeyValuePair<K, V>> list = new CSharpList<>();
        for (Map.Entry<K, V> entry : items.entrySet()) {
            list.add(new CSharpKeyValuePair<>(entry.getKey(), entry.getValue()));
        }
        return CSharpGenericEnumerator.from(list.iterator());
    }

    @Override
    public ImmutableSortedDictionary<K, V> add(K key, V value) {
        TreeMap<K, V> copy = new TreeMap<>(items);
        copy.put(key, value);
        return new ImmutableSortedDictionary<>(copy);
    }

    public V tryGetValue(K key) {
        return items.get(key);
    }

    // No fluent clear(): realizes CSharpReadOnlyDict, whose CSharpGenericIterable ancestor
    // declares void clear(); generated code never calls Clear() on an immutable dictionary.

    @Override
    public ImmutableSortedDictionary<K, V> removeCSharp(K key) {
        TreeMap<K, V> copy = new TreeMap<>(items);
        copy.remove(key);
        return new ImmutableSortedDictionary<>(copy);
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
