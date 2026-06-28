package io.github.ningpp.compat;

public interface IDictionary extends CSharpCollection {
    void add(Object key, Object value);

    void clear();

    boolean contains(Object key);

    boolean getIsFixedSize();

    boolean getIsReadOnly();

    CSharpCollection getKeys();

    void remove(Object key);

    CSharpCollection getValues();

    Object get(Object key);

    Object put(Object key, Object value);

    default java.util.Set<java.util.Map.Entry<Object, Object>> entrySet() {
        java.util.LinkedHashSet<java.util.Map.Entry<Object, Object>> entries = new java.util.LinkedHashSet<>();
        for (Object item : this) {
            if (item instanceof java.util.Map.Entry<?, ?> entry) {
                entries.add(new DictionaryEntry(entry.getKey(), entry.getValue()));
            }
        }
        return entries;
    }
}
