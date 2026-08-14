package io.github.ningpp.compat;

public interface CSharpReadOnlyDict<K, V> extends CSharpReadOnlyCollection<CSharpKeyValuePair<K, V>> {
    boolean containsKey(K key);
    V get(K key);

    default CSharpGenericIterable<K> keySet() {
        CSharpList<K> keys = new CSharpList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            keys.add(pair.getKey());
        }
        return keys;
    }

    default CSharpGenericIterable<V> values() {
        CSharpList<V> vals = new CSharpList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            vals.add(pair.getValue());
        }
        return vals;
    }

    V tryGetValue(K key);
}
