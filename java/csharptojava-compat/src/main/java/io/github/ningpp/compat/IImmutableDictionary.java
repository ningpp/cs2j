package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Immutable.IImmutableDictionary&lt;K,V&gt;.
 */
public interface IImmutableDictionary<K, V> extends Iterable<Map.Entry<K, V>> {
    int size();

    boolean isEmpty();

    boolean containsKey(K key);

    V get(K key);

    Set<K> keySet();

    Set<Map.Entry<K, V>> entrySet();
}
