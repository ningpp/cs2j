package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpDictionary<K, V> implements CSharpGenericIDictionary<K, V>, Cloneable {
    private final LinkedHashMap<K, V> map;
    private final CSharpGenericEqualityComparer<K> comparer;

    public CSharpDictionary() {
        this.map = new LinkedHashMap<>();
        this.comparer = null;
    }

    public CSharpDictionary(int capacity) {
        this.map = new LinkedHashMap<>(capacity);
        this.comparer = null;
    }

    public CSharpDictionary(Map<? extends K, ? extends V> m) {
        this.map = new LinkedHashMap<>(m);
        this.comparer = null;
    }

    // CSharpGenericIDictionary / CSharpICollection methods

    @Override
    public void add(K key, V value) {
        if (map.containsKey(key)) {
            throw new IllegalArgumentException("An element with the same key already exists: " + key);
        }
        map.put(key, value);
    }

    @Override
    public boolean containsKey(Object key) {
        return map.containsKey(key);
    }

    @Override
    public boolean containsValue(Object value) {
        return map.containsValue(value);
    }

    @Override
    public boolean remove(Object o) {
        if (o instanceof Map.Entry<?, ?> entry) {
            Object key = entry.getKey();
            if (map.containsKey(key) && Objects.equals(map.get(key), entry.getValue())) {
                map.remove(key);
                return true;
            }
            return false;
        }
        return map.remove(o) != null;
    }

    @Override
    public V get(Object key) {
        return map.get(key);
    }

    @Override
    public V put(K key, V value) {
        return map.put(key, value);
    }

    @Override
    public CSharpICollection<K> getKeys() {
        return new KeyCollection<>(this);
    }

    @Override
    public CSharpICollection<V> getValues() {
        return new ValueCollection<>(this);
    }

    @Override
    public V tryGetValue(K key) {
        return map.get(key);
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public int getCount() {
        return map.size();
    }

    // CSharpICollection<CSharpKeyValuePair<K, V>> methods

    @Override
    public boolean add(CSharpKeyValuePair<K, V> item) {
        if (map.containsKey(item.getKey())) {
            return false;
        }
        map.put(item.getKey(), item.getValue());
        return true;
    }

    @Override
    public void clear() {
        map.clear();
    }

    @Override
    public boolean contains(Object o) {
        if (o instanceof Map.Entry<?, ?> entry) {
            Object key = entry.getKey();
            if (!map.containsKey(key)) return false;
            return Objects.equals(map.get(key), entry.getValue());
        }
        return false;
    }

    @Override
    public void copyTo(CSharpKeyValuePair<K, V>[] array, int arrayIndex) {
        int i = arrayIndex;
        for (Map.Entry<K, V> e : map.entrySet()) {
            array[i++] = new CSharpKeyValuePair<>(e.getKey(), e.getValue());
        }
    }

    // Dictionary-specific methods

    public boolean tryAdd(K key, V value) {
        if (map.containsKey(key)) {
            return false;
        }
        map.put(key, value);
        return true;
    }

    public int ensureCapacity(int minCapacity) {
        // LinkedHashMap doesn't expose ensureCapacity, but we can hint
        // Return current size as capacity is not directly exposed
        return map.size();
    }

    @Override
    public CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> iterator() {
        return CSharpGenericEnumerator.from(new Iterator<CSharpKeyValuePair<K, V>>() {
            private final Iterator<Map.Entry<K, V>> it = map.entrySet().iterator();

            @Override
            public boolean hasNext() {
                return it.hasNext();
            }

            @Override
            public CSharpKeyValuePair<K, V> next() {
                Map.Entry<K, V> e = it.next();
                return new CSharpKeyValuePair<>(e.getKey(), e.getValue());
            }
        });
    }

    public CSharpGenericEqualityComparer<K> getComparer() {
        return comparer;
    }

    @Override
    public CSharpDictionary<K, V> clone() {
        try {
            return new CSharpDictionary<>(map);
        } catch (Exception e) {
            throw new InternalError();
        }
    }

    // Static nested classes for KeyCollection and ValueCollection

    public static class KeyCollection<K, V> implements CSharpICollection<K> {
        private final CSharpDictionary<K, V> dict;

        public KeyCollection(CSharpDictionary<K, V> dict) {
            this.dict = dict;
        }

        @Override
        public boolean add(K item) {
            throw new UnsupportedOperationException("KeyCollection is read-only");
        }

        @Override
        public void clear() {
            throw new UnsupportedOperationException("KeyCollection is read-only");
        }

        @Override
        public boolean contains(Object o) {
            return dict.containsKey(o);
        }

        @Override
        public void copyTo(K[] array, int arrayIndex) {
            int i = arrayIndex;
            for (K key : dict.map.keySet()) {
                array[i++] = key;
            }
        }

        @Override
        public boolean remove(Object o) {
            throw new UnsupportedOperationException("KeyCollection is read-only");
        }

        @Override
        public int getCount() {
            return dict.getCount();
        }

        @Override
        public boolean getIsReadOnly() {
            return true;
        }

        @Override
        public CSharpGenericEnumerator<K> iterator() {
            return CSharpGenericEnumerator.from(dict.map.keySet().iterator());
        }
    }

    public static class ValueCollection<K, V> implements CSharpICollection<V> {
        private final CSharpDictionary<K, V> dict;

        public ValueCollection(CSharpDictionary<K, V> dict) {
            this.dict = dict;
        }

        @Override
        public boolean add(V item) {
            throw new UnsupportedOperationException("ValueCollection is read-only");
        }

        @Override
        public void clear() {
            throw new UnsupportedOperationException("ValueCollection is read-only");
        }

        @Override
        public boolean contains(Object o) {
            return dict.containsValue(o);
        }

        @Override
        public void copyTo(V[] array, int arrayIndex) {
            int i = arrayIndex;
            for (V value : dict.map.values()) {
                array[i++] = value;
            }
        }

        @Override
        public boolean remove(Object o) {
            throw new UnsupportedOperationException("ValueCollection is read-only");
        }

        @Override
        public int getCount() {
            return dict.getCount();
        }

        @Override
        public boolean getIsReadOnly() {
            return true;
        }

        @Override
        public CSharpGenericEnumerator<V> iterator() {
            return CSharpGenericEnumerator.from(dict.map.values().iterator());
        }
    }
}
