package io.github.ningpp.compat;

import java.util.Comparator;
import java.util.Iterator;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;

public class CSharpSortedDict<K, V> implements CSharpGenericIDictionary<K, V>, Cloneable {
    private final TreeMap<K, V> map;
    private final CSharpGenericComparer<K> comparer;

    public CSharpSortedDict() {
        this.map = new TreeMap<>();
        this.comparer = null;
    }

    public CSharpSortedDict(Comparator<? super K> comparator) {
        this.map = new TreeMap<>(comparator);
        this.comparer = null;
    }

    public CSharpSortedDict(Map<? extends K, ? extends V> m) {
        this.map = new TreeMap<>(m);
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
    public V get(K key) {
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

    // Map-like access methods

    public java.util.Set<K> keySet() {
        return map.keySet();
    }

    public java.util.Collection<V> values() {
        return map.values();
    }

    public java.util.Set<Map.Entry<K, V>> entrySet() {
        return map.entrySet();
    }

    public V getOrDefault(K key, V defaultValue) {
        return map.getOrDefault(key, defaultValue);
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

    public CSharpGenericComparer<K> getComparer() {
        if (comparer != null) {
            return comparer;
        }
        final Comparator<? super K> comp = map.comparator();
        if (comp != null) {
            return comp::compare;
        }
        @SuppressWarnings("unchecked")
        CSharpGenericComparer<K> defaultComparer = (a, b) -> ((Comparable<Object>) a).compareTo(b);
        return defaultComparer;
    }

    @Override
    public CSharpSortedDict<K, V> clone() {
        try {
            return new CSharpSortedDict<>(map);
        } catch (Exception e) {
            throw new InternalError();
        }
    }

    // Static nested classes for KeyCollection and ValueCollection

    public static class KeyCollection<K, V> implements CSharpICollection<K> {
        private final CSharpSortedDict<K, V> dict;

        public KeyCollection(CSharpSortedDict<K, V> dict) {
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
        private final CSharpSortedDict<K, V> dict;

        public ValueCollection(CSharpSortedDict<K, V> dict) {
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
