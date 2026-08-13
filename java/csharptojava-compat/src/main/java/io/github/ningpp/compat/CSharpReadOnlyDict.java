package io.github.ningpp.compat;

public interface CSharpReadOnlyDict<K, V> extends CSharpReadOnlyCollection<CSharpKeyValuePair<K, V>> {
    boolean containsKey(Object key);
    V get(K key);
    CSharpReadOnlyCollection<K> getKeys();
    CSharpReadOnlyCollection<V> getValues();
    V tryGetValue(K key);
}
