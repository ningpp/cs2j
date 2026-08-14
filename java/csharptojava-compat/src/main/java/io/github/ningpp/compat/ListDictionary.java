package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.ListDictionary.
 */
public class ListDictionary implements IOrderedDictionary {
    private final Map<Object, Object> items = new LinkedHashMap<>();

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean containsKey(Object key) {
        return items.containsKey(key);
    }

    public Object get(Object key) {
        return items.get(key);
    }

    public Object put(Object key, Object value) {
        return items.put(key, value);
    }

    public Set<Object> keySet() {
        return items.keySet();
    }

    public Set<Map.Entry<Object, Object>> entrySet() {
        return items.entrySet();
    }

    public java.util.Iterator<Map.Entry<Object, Object>> iterator() {
        return items.entrySet().iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
