package io.github.ningpp.compat;

public final class DictionaryEntry implements java.util.Map.Entry<Object, Object> {
    private final Object key;
    private Object value;

    public DictionaryEntry(Object key, Object value) {
        this.key = key;
        this.value = value;
    }

    @Override
    public Object getKey() {
        return key;
    }

    @Override
    public Object getValue() {
        return value;
    }

    @Override
    public Object setValue(Object value) {
        Object previous = this.value;
        this.value = value;
        return previous;
    }
}
