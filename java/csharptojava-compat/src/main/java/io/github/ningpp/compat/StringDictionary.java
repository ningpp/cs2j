package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.StringDictionary.
 */
public class StringDictionary {
    private final Map<String, String> items = new LinkedHashMap<>();

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    public boolean containsKey(String key) {
        return items.containsKey(key);
    }

    public String get(String key) {
        return items.get(key);
    }

    public String put(String key, String value) {
        return items.put(key, value);
    }

    public Set<String> keySet() {
        return items.keySet();
    }

    public Set<Map.Entry<String, String>> entrySet() {
        return items.entrySet();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
