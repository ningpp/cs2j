package io.github.ningpp.compat;

import java.util.Map;

public final class CSharpDictEntry implements Map.Entry<Object, Object> {
    private final Object key;
    private Object value;

    public CSharpDictEntry(Object key, Object value) {
        this.key = key;
        this.value = value;
    }

    @Override public Object getKey() { return key; }
    @Override public Object getValue() { return value; }
    @Override public Object setValue(Object value) {
        Object previous = this.value;
        this.value = value;
        return previous;
    }
}
