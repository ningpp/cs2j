package io.github.ningpp.compat;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Compat for System.Collections.Specialized.NameValueCollection.
 */
public class NameValueCollection {
    private final Map<String, String> items = new LinkedHashMap<>();

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public String get(String name) {
        return items.get(name);
    }

    public String get(int index) {
        int i = 0;
        for (String value : items.values()) {
            if (i++ == index) {
                return value;
            }
        }
        return null;
    }

    public String getKey(int index) {
        int i = 0;
        for (String key : items.keySet()) {
            if (i++ == index) {
                return key;
            }
        }
        return null;
    }

    public void add(String name, String value) {
        items.put(name, value);
    }

    public void set(String name, String value) {
        items.put(name, value);
    }

    public Set<String> keySet() {
        return items.keySet();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
