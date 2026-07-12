package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;

public class CSharpSortedList<K, V> implements CSharpGenericIDictionary<K, V>, Cloneable {
    private final TreeMap<K, V> map;
    private final CSharpGenericComparer<K> comparer;

    public CSharpSortedList() {
        this.map = new TreeMap<>();
        this.comparer = null;
    }

    public CSharpSortedList(Comparator<? super K> comparator) {
        this.map = new TreeMap<>(comparator);
        this.comparer = null;
    }

    public CSharpSortedList(Map<? extends K, ? extends V> m) {
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
    public V get(Object key) {
        return map.get(key);
    }

    @Override
    public V put(K key, V value) {
        return map.put(key, value);
    }

    @Override
    public CSharpList<K> getKeys() {
        return new CSharpList<>(map.keySet());
    }

    @Override
    public CSharpList<V> getValues() {
        return new CSharpList<>(map.values());
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

    // Index-based access (specific to SortedList)

    private List<K> keyList() {
        return new ArrayList<>(map.keySet());
    }

    private List<V> valueList() {
        return new ArrayList<>(map.values());
    }

    public K getKeyAtIndex(int index) {
        int i = 0;
        for (K key : map.keySet()) {
            if (i++ == index) return key;
        }
        throw new IndexOutOfBoundsException("Index: " + index);
    }

    public V getValueAtIndex(int index) {
        int i = 0;
        for (V value : map.values()) {
            if (i++ == index) return value;
        }
        throw new IndexOutOfBoundsException("Index: " + index);
    }

    public int indexOfKey(Object key) {
        int i = 0;
        for (K k : map.keySet()) {
            if (Objects.equals(k, key)) return i;
            i++;
        }
        return -1;
    }

    public int indexOfValue(Object value) {
        int i = 0;
        for (V v : map.values()) {
            if (Objects.equals(v, value)) return i;
            i++;
        }
        return -1;
    }

    public int ensureCapacity(int capacity) {
        // TreeMap has no capacity concept; no-op
        return map.size();
    }

    public void trimExcess() {
        // TreeMap has no capacity concept; no-op
    }

    @Override
    public CSharpSortedList<K, V> clone() {
        try {
            return new CSharpSortedList<>(map);
        } catch (Exception e) {
            throw new InternalError();
        }
    }

    // Static nested classes for KeyCollection and ValueCollection

    public static class KeyCollection<K, V> implements CSharpICollection<K> {
        private final CSharpSortedList<K, V> dict;

        public KeyCollection(CSharpSortedList<K, V> dict) {
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

        public K get(int index) {
            int i = 0;
            for (K key : dict.map.keySet()) {
                if (i == index) return key;
                i++;
            }
            throw new IndexOutOfBoundsException(index);
        }
    }

    public static class ValueCollection<K, V> implements CSharpICollection<V> {
        private final CSharpSortedList<K, V> dict;

        public ValueCollection(CSharpSortedList<K, V> dict) {
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

        public V get(int index) {
            int i = 0;
            for (V value : dict.map.values()) {
                if (i == index) return value;
                i++;
            }
            throw new IndexOutOfBoundsException(index);
        }
    }
}
