package io.github.ningpp.compat;

/**
 * Compat for System.Collections.ObjectModel.ReadOnlyDictionary&lt;K,V&gt; — a
 * read-only dictionary view over a CSharpDictionary.
 */
public class ReadOnlyDictionary<K, V> implements CSharpReadOnlyDict<K, V> {
    private final CSharpDictionary<K, V> items;

    public ReadOnlyDictionary() {
        this.items = new CSharpDictionary<>();
    }

    public ReadOnlyDictionary(CSharpDictionary<K, V> source) {
        this.items = new CSharpDictionary<>(source);
    }

    @Override
    public boolean containsKey(Object key) {
        return items.containsKey(key);
    }

    @Override
    public V get(K key) {
        return items.get(key);
    }

    @Override
    public V tryGetValue(K key) {
        return items.tryGetValue(key);
    }

    @Override
    public CSharpReadOnlyCollection<K> getKeys() {
        return items.getKeys();
    }

    @Override
    public CSharpReadOnlyCollection<V> getValues() {
        return items.getValues();
    }

    @Override
    public int getCount() {
        return items.getCount();
    }

    public int size() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    @Override
    public CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> iterator() {
        return items.iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
