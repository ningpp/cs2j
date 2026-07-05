package io.github.ningpp.compat;

import java.util.List;
import java.util.Map;

/**
 * Utilities for emulating C# assignment-expression semantics on Java collections.
 *
 * <p>In C#, {@code dict[key] = value} evaluates to the <em>assigned</em> value.
 * In Java, {@code map.put(key, value)} evaluates to the <em>previous</em> value
 * (or {@code null}).  These helpers bridge that gap when the indexer assignment
 * result is used as an expression value rather than a standalone statement.</p>
 */
public class MapHelper {

    /**
     * Put a key-value pair into the map and return the <em>inserted</em> value,
     * matching C# {@code dict[key] = value} expression semantics.
     */
    public static <K, V> V putValue(Map<K, V> map, K key, V value) {
        map.put(key, value);
        return value;
    }

    public static <K, V> V putValue(CSharpGenericIDictionary<K, V> dict, K key, V value) {
        dict.put(key, value);
        return value;
    }

    /**
     * Set an element at the given index and return the <em>inserted</em> value,
     * matching C# {@code list[index] = value} expression semantics.
     */
    public static <T> T setValue(List<T> list, int index, T value) {
        list.set(index, value);
        return value;
    }
}
