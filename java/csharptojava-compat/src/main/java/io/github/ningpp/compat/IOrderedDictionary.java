package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.IOrderedDictionary.
 */
public interface IOrderedDictionary extends Iterable<Map.Entry<Object, Object>> {
    int size();

    boolean isEmpty();

    boolean containsKey(Object key);

    Object get(Object key);

    Object put(Object key, Object value);

    Set<Object> keySet();

    Set<Map.Entry<Object, Object>> entrySet();
}
