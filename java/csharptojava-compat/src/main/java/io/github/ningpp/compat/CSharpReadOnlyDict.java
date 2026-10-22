package io.github.ningpp.compat;

/**
 * C# IReadOnlyDictionary&lt;K,V&gt; compat view. Deliberately does not extend
 * CSharpReadOnlyCollection: a generated dictionary collection also implements
 * CSharpIList (whose Collection&lt;Object&gt; base declares add(Object)), and
 * inheriting Collection&lt;KeyValuePair&gt; from this interface would make the
 * class inherit the same erased add with two different type arguments.
 */
public interface CSharpReadOnlyDict<K, V> {
    int getCount();
    CSharpGenericEnumerator iterator();
    boolean containsKey(K key);
    V get(K key);

    /**
     * Mirrors IReadOnlyDictionary&lt;TKey, TValue&gt;.get_Item: throws
     * KeyNotFoundException when the key is absent. Emitted for C# indexer reads.
     */
    default V getChecked(K key) {
        if (!containsKey(key)) {
            throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
        }
        return get(key);
    }

    @SuppressWarnings("unchecked")
    default CSharpGenericIterable<K> keySet() {
        CSharpList<K> keys = new CSharpList<>();
        CSharpGenericEnumerator e = iterator();
        while (e.moveNext()) {
            keys.add(((CSharpKeyValuePair<K, V>) e.getCurrent()).getKey());
        }
        return keys;
    }

    @SuppressWarnings("unchecked")
    default CSharpGenericIterable<V> values() {
        CSharpList<V> vals = new CSharpList<>();
        CSharpGenericEnumerator e = iterator();
        while (e.moveNext()) {
            vals.add(((CSharpKeyValuePair<K, V>) e.getCurrent()).getValue());
        }
        return vals;
    }

    default V tryGetValue(K key) {
        return get(key);
    }
}
