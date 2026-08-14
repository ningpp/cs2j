package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.IOrderedDictionary.
 * Intentionally NOT Iterable — OrderedDictionary implements CSharpIDictionary
 * whose CSharpIterable<Object> inheritance would clash with a Map.Entry iteration.
 */
public interface IOrderedDictionary {
    int size();

    boolean isEmpty();

    boolean containsKey(Object key);

    Object get(Object key);

    Object put(Object key, Object value);

    Set<Object> keySet();

    Set<Map.Entry<Object, Object>> entrySet();
}
