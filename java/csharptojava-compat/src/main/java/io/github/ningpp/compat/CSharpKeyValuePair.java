package io.github.ningpp.compat;

import java.util.Map;

public final class CSharpKeyValuePair<K, V> implements Map.Entry<K, V> {
    private final K key;
    private V value;

    public CSharpKeyValuePair(K key, V value) {
        this.key = key;
        this.value = value;
    }

    @Override
    public K getKey() {
        return key;
    }

    @Override
    public V getValue() {
        return value;
    }

    @Override
    public V setValue(V value) {
        V old = this.value;
        this.value = value;
        return old;
    }

    @Override
    public String toString() {
        return "[" + key + ", " + value + "]";
    }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof Map.Entry<?, ?> e)) return false;
        Object k1 = getKey();
        Object k2 = e.getKey();
        if (k1 == null ? k2 == null : k1.equals(k2)) {
            Object v1 = getValue();
            Object v2 = e.getValue();
            return v1 == null ? v2 == null : v1.equals(v2);
        }
        return false;
    }

    @Override
    public int hashCode() {
        return (key == null ? 0 : key.hashCode()) ^
               (value == null ? 0 : value.hashCode());
    }
}
