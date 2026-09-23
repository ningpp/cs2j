package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpDictionary<K, V> implements CSharpGenericIDictionary<K, V>, CSharpReadOnlyDict<K, V>, Cloneable {
    // Keys are stored wrapped in ComparerKey when a comparer was supplied, so the
    // backing map hashes/compares through the comparer like C# Dictionary<TKey, TValue>
    // instead of through the key's own equals/hashCode (see ComparerKey).
    private final LinkedHashMap<Object, V> map;
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

    public CSharpDictionary(CSharpDictionary<? extends K, ? extends V> other) {
        this.map = new LinkedHashMap<>();
        @SuppressWarnings("unchecked")
        CSharpGenericEqualityComparer<K> otherComparer = (CSharpGenericEqualityComparer<K>) other.comparer;
        this.comparer = otherComparer;
        for (Map.Entry<Object, ? extends V> entry : other.map.entrySet()) {
            this.map.put(this.wrapKey(rawMapKey(entry.getKey())), entry.getValue());
        }
    }

    public CSharpDictionary(CSharpGenericIDictionary<? extends K, ? extends V> items) {
        this.map = new LinkedHashMap<>();
        for (CSharpKeyValuePair<? extends K, ? extends V> pair : items) {
            this.map.put(pair.getKey(), pair.getValue());
        }
        this.comparer = null;
    }

    public CSharpDictionary(CSharpGenericEqualityComparer<K> comparer) {
        this.map = new LinkedHashMap<>();
        this.comparer = comparer;
    }

    public CSharpDictionary(int capacity, CSharpGenericEqualityComparer<K> comparer) {
        this.map = new LinkedHashMap<>(capacity);
        this.comparer = comparer;
    }

    /**
     * The key stored in the backing map for {@code key}: wrapped in a ComparerKey
     * when a comparer was supplied, the key itself otherwise. Null keys are never
     * wrapped (the backing map keeps them under the raw null key).
     */
    private Object wrapKey(Object key) {
        if (comparer == null || key == null) {
            return key;
        }
        @SuppressWarnings("unchecked")
        ComparerKey<K> wrapped = new ComparerKey<>((K) key, comparer);
        return wrapped;
    }

    /** The raw (user-visible) key for a key as stored in the backing map. */
    @SuppressWarnings("unchecked")
    private K unwrapKey(Object mapKey) {
        if (comparer == null) {
            return (K) mapKey;
        }
        return mapKey instanceof ComparerKey<?> wrapped ? (K) wrapped.key : (K) mapKey;
    }

    /** The raw key behind a backing-map key, regardless of which dictionary stored it. */
    private static Object rawMapKey(Object mapKey) {
        return mapKey instanceof ComparerKey<?> wrapped ? wrapped.key : mapKey;
    }

    // CSharpGenericIDictionary / CSharpICollection methods

    /**
     * The stored key this one is EQUAL to under the dictionary's comparer, or null.
     *
     * <p>C# dictionaries compare keys with the comparer they were built with; the Java map
     * underneath uses {@code Object.equals}, so a {@code StringComparer.OrdinalIgnoreCase}
     * dictionary answered "not found" for every case variant. Measured: the serializer's property
     * cache could not match {@code "MYSTRING"} to a property named {@code MyString}, so
     * {@code PropertyNameCaseInsensitive} did nothing and the second of two names differing only in
     * case was dropped ({@code jsonIgnoreCondition_LastOneWins}, {@code honorCaseInsensitivity}).</p>
     *
     * <p>When a comparer was supplied the backing map hashes and compares keys through it
     * (ComparerKey), so the scan still returns the key already stored, which lets a later
     * {@code put} replace its value without changing the key's original spelling — C#
     * behaviour.</p>
     */
    @SuppressWarnings("unchecked")
    private K storedKey(Object key) {
        if (key == null) {
            return null;
        }
        if (comparer == null) {
            return map.containsKey(key) ? (K) key : null;
        }
        for (Object stored : map.keySet()) {
            K storedRaw = unwrapKey(stored);
            if (storedRaw != null && comparer.equals(storedRaw, (K) key)) {
                return storedRaw;
            }
        }
        return null;
    }

    @Override
    public void add(K key, V value) {
        if (storedKey(key) != null) {
            throw new ArgumentException("An element with the same key already exists: " + key);
        }
        map.put(wrapKey(key), value);
    }

    @Override
    public boolean containsKey(K key) {
        if (storedKey(key) != null) {
            return true;
        }
        if (key instanceof Class) {
            Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
            if (normalized != key && map.containsKey(wrapKey(normalized))) {
                return true;
            }
            Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
            if (denormalized != key && map.containsKey(wrapKey(denormalized))) {
                return true;
            }
        }
        return false;
    }

    @Override
    public boolean containsValue(Object value) {
        return map.containsValue(value);
    }

    @Override
    @SuppressWarnings("unchecked")
    public boolean remove(Object o) {
        if (o instanceof Map.Entry<?, ?> entry) {
            K key = storedKey(entry.getKey());
            if (key != null && Objects.equals(map.get(wrapKey(key)), entry.getValue())) {
                map.remove(wrapKey(key));
                return true;
            }
            return false;
        }
        K key = storedKey(o);
        return key != null && map.remove(wrapKey(key)) != null;
    }

    @Override
    @SuppressWarnings("unchecked")
    public V get(K key) {
        K stored = storedKey(key);
        V v = stored == null ? null : map.get(wrapKey(stored));
        if (v == null && key instanceof Class) {
            // Bridge C# type-token classes (CSharpSByte, CSharpByte, etc.)
            // to their Java wrapper equivalents so that dictionary
            // lookups succeed regardless of which form was used as key.
            Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
            if (normalized != key) {
                v = map.get(wrapKey(normalized));
            }
            // Also try the reverse: Java wrapper classes (e.g. Byte.class)
            // to C# type-token classes (e.g. CSharpByte.class) so that
            // lookups for Byte.class find converters registered under
            // CSharpByte.class.
            if (v == null) {
                Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
                if (denormalized != key) {
                    v = map.get(wrapKey(denormalized));
                }
            }
        }
        return v;
    }

    @Override
    @SuppressWarnings("unchecked")
    public V getChecked(K key) {
        if (!map.containsKey(wrapKey(key))) {
            // Try normalized and denormalized lookups for Class keys, mirroring
            // containsKey()/get() so all read paths bridge type tokens the
            // same way.
            if (key instanceof Class) {
                Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
                if (normalized != key && map.containsKey(wrapKey(normalized))) {
                    return map.get(wrapKey(normalized));
                }
                Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
                if (denormalized != key && map.containsKey(wrapKey(denormalized))) {
                    return map.get(wrapKey(denormalized));
                }
            }
            K stored = storedKey(key);
            if (stored == null) {
                throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
            }
            return map.get(wrapKey(stored));
        }
        return map.get(wrapKey(key));
    }

    @Override
    public V put(K key, V value) {
        // C# keeps the key that is already stored; only the value is replaced.
        K stored = storedKey(key);
        return map.put(stored == null ? wrapKey(key) : wrapKey(stored), value);
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
        return get(key);
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public int getCount() {
        return map.size();
    }

    @Override
    public int size() {
        return map.size();
    }

    @Override
    public boolean isEmpty() {
        return map.isEmpty();
    }

    @Override
    public Object[] toArray() {
        java.util.List<CSharpKeyValuePair<K, V>> list = new java.util.ArrayList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            list.add(pair);
        }
        return list.toArray();
    }

    @Override
    public <E> E[] toArray(E[] a) {
        java.util.List<CSharpKeyValuePair<K, V>> list = new java.util.ArrayList<>();
        for (CSharpKeyValuePair<K, V> pair : this) {
            list.add(pair);
        }
        return list.toArray(a);
    }

    // CSharpICollection<CSharpKeyValuePair<K, V>> methods

    @Override
    public boolean add(CSharpKeyValuePair<K, V> item) {
        if (storedKey(item.getKey()) != null) {
            return false;
        }
        map.put(wrapKey(item.getKey()), item.getValue());
        return true;
    }

    @Override
    public void clear() {
        map.clear();
    }

    @Override
    public boolean contains(Object o) {
        if (o instanceof Map.Entry<?, ?> entry) {
            K key = storedKey(entry.getKey());
            if (key == null) return false;
            return Objects.equals(map.get(wrapKey(key)), entry.getValue());
        }
        return false;
    }

    @Override
    public void copyTo(CSharpKeyValuePair<K, V>[] array, int arrayIndex) {
        int i = arrayIndex;
        for (Map.Entry<Object, V> e : map.entrySet()) {
            array[i++] = new CSharpKeyValuePair<>(unwrapKey(e.getKey()), e.getValue());
        }
    }

    // Dictionary-specific methods

    public V getOrDefault(K key, V defaultValue) {
        V v = get(key);
        return v == null ? defaultValue : v;
    }

    public CSharpGenericIterableSet<K> keySet() {
        return new CSharpGenericIterableSet<>(new java.util.AbstractSet<K>() {
            @Override
            public java.util.Iterator<K> iterator() {
                java.util.Iterator<Object> it = map.keySet().iterator();
                return new java.util.Iterator<K>() {
                    @Override
                    public boolean hasNext() {
                        return it.hasNext();
                    }

                    @Override
                    public K next() {
                        return unwrapKey(it.next());
                    }

                    @Override
                    public void remove() {
                        it.remove();
                    }
                };
            }

            @Override
            public int size() {
                return map.size();
            }

            @Override
            public boolean contains(Object o) {
                return storedKey(o) != null;
            }
        });
    }

    public CSharpGenericIterable<V> values() {
        return CSharpGenericIterable.from(map.values());
    }

    /** Mirrors Dictionary&lt;TKey, TValue&gt;.Values.CopyTo(T[], int) — used by generated code. */
    public void copyValuesTo(V[] array, int arrayIndex) {
        java.util.List<V> values = new java.util.ArrayList<>(map.values());
        for (int i = 0; i < values.size() && arrayIndex + i < array.length; i++) {
            array[arrayIndex + i] = values.get(i);
        }
    }

    public java.util.Set<Map.Entry<K, V>> entrySet() {
        return new java.util.AbstractSet<Map.Entry<K, V>>() {
            @Override
            public java.util.Iterator<Map.Entry<K, V>> iterator() {
                java.util.Iterator<Map.Entry<Object, V>> it = map.entrySet().iterator();
                return new java.util.Iterator<Map.Entry<K, V>>() {
                    @Override
                    public boolean hasNext() {
                        return it.hasNext();
                    }

                    @Override
                    public Map.Entry<K, V> next() {
                        Map.Entry<Object, V> e = it.next();
                        return new java.util.AbstractMap.SimpleEntry<>(unwrapKey(e.getKey()), e.getValue());
                    }

                    @Override
                    public void remove() {
                        it.remove();
                    }
                };
            }

            @Override
            public int size() {
                return map.size();
            }
        };
    }

    public boolean tryAdd(K key, V value) {
        if (storedKey(key) != null) {
            return false;
        }
        map.put(wrapKey(key), value);
        return true;
    }

    public int ensureCapacity(int minCapacity) {
        // LinkedHashMap doesn't expose ensureCapacity, but we can hint
        // Return current size as capacity is not directly exposed
        return map.size();
    }

    @Override
    public Enumerator<K, V> iterator() {
        return new Enumerator<>(map.entrySet().iterator());
    }

    /**
     * Mirrors Dictionary&lt;TKey, TValue&gt;.Enumerator — the C# generic dictionary
     * enumerator. The converter references it as Dictionary.Enumerator (raw).
     */
    public static class Enumerator<K, V> implements CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> {
        private final Iterator<Map.Entry<Object, V>> it;
        private CSharpKeyValuePair<K, V> current;
        private boolean hasCurrent;

        public Enumerator(Iterator<Map.Entry<Object, V>> it) {
            this.it = it;
        }

        @Override
        public boolean moveNext() {
            if (!it.hasNext()) {
                current = null;
                hasCurrent = false;
                return false;
            }
            Map.Entry<Object, V> e = it.next();
            current = new CSharpKeyValuePair<>((K) CSharpDictionary.rawMapKey(e.getKey()), e.getValue());
            hasCurrent = true;
            return true;
        }

        @Override
        public CSharpKeyValuePair<K, V> getCurrent() {
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current;
        }

        @Override
        public boolean hasNext() {
            return it.hasNext();
        }

        @Override
        public CSharpKeyValuePair<K, V> next() {
            if (!moveNext()) throw new java.util.NoSuchElementException();
            return current;
        }

        @Override
        public void remove() {
            it.remove();
        }
    }

    public CSharpGenericEqualityComparer<K> getComparer() {
        return comparer;
    }

    @Override
    @SuppressWarnings("unchecked")
    public CSharpDictionary<K, V> clone() {
        try {
            if (comparer == null) {
                return new CSharpDictionary<>((Map<? extends K, ? extends V>) this.map);
            }
            CSharpDictionary<K, V> copy = new CSharpDictionary<>(comparer);
            for (Map.Entry<Object, V> e : this.map.entrySet()) {
                copy.map.put(copy.wrapKey(unwrapKey(e.getKey())), e.getValue());
            }
            return copy;
        } catch (Exception e) {
            throw new InternalError();
        }
    }

    /**
     * Map key wrapper that hashes and compares through the dictionary's comparer —
     * C# Dictionary&lt;TKey, TValue&gt; always routes GetHashCode/Equals through its
     * comparer, never through the key itself. Two failures came from the backing
     * map hashing raw keys:
     *
     * <ul>
     * <li>the Preserve reference resolver keys a CSharpDictionary by object identity,
     * and a circular CSharpList key overflowed the stack in ArrayList.hashCode()
     * (ReferenceHandlerTests.arrayLoop);</li>
     * <li>a POCO whose GetHashCode() is unstable changed its hash between insert and
     * lookup, so the stored id could not be found again and "$ref":null was written
     * (ReferenceHandlerTests.customHashCode).</li>
     * </ul>
     */
    private static final class ComparerKey<K> {
        private final K key;
        private final CSharpGenericEqualityComparer<K> comparer;

        ComparerKey(K key, CSharpGenericEqualityComparer<K> comparer) {
            this.key = key;
            this.comparer = comparer;
        }

        @Override
        public boolean equals(Object other) {
            return other instanceof ComparerKey<?> otherKey
                    && comparer.equals(key, (K) otherKey.key);
        }

        @Override
        public int hashCode() {
            return comparer.hashCode(key);
        }

        @Override
        public String toString() {
            return String.valueOf(key);
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
            @SuppressWarnings("unchecked")
            K key = (K) o;
            return dict.containsKey(key);
        }

        @Override
        public void copyTo(K[] array, int arrayIndex) {
            int i = arrayIndex;
            for (Object key : dict.map.keySet()) {
                array[i++] = dict.unwrapKey(key);
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
            java.util.Iterator<Object> it = dict.map.keySet().iterator();
            return CSharpGenericEnumerator.from(new java.util.Iterator<K>() {
                @Override
                public boolean hasNext() {
                    return it.hasNext();
                }

                @Override
                public K next() {
                    return dict.unwrapKey(it.next());
                }

                @Override
                public void remove() {
                    it.remove();
                }
            });
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

