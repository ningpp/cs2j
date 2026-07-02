package io.github.ningpp.compat;

import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

public interface CSharpIDictionary extends CSharpCollection {
    void add(Object key, Object value);
    void clear();
    boolean contains(Object key);
    boolean getIsFixedSize();
    boolean getIsReadOnly();
    CSharpCollection getKeys();
    CSharpCollection getValues();
    void remove(Object key);
    Object get(Object key);
    Object put(Object key, Object value);

    default Set<Map.Entry<Object, Object>> entrySet() {
        LinkedHashSet<Map.Entry<Object, Object>> entries = new LinkedHashSet<>();
        for (Object item : this) {
            if (item instanceof Map.Entry<?, ?> entry) {
                entries.add(new CSharpDictEntry(entry.getKey(), entry.getValue()));
            }
        }
        return entries;
    }
}
