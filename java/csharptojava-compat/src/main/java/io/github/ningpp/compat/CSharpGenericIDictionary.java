package io.github.ningpp.compat;

public interface CSharpGenericIDictionary<K, V> extends CSharpICollection<CSharpKeyValuePair<K, V>> {
    void add(K key, V value);
    boolean containsKey(Object key);
    boolean containsValue(Object value);
    boolean remove(Object key);
    V get(Object key);
    V put(K key, V value);
    CSharpICollection<K> getKeys();
    CSharpICollection<V> getValues();
    V tryGetValue(K key);
    boolean getIsReadOnly();
    int getCount();
}
