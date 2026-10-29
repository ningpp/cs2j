package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpDictionary<K, V> implements CSharpGenericIDictionary<K, V>, CSharpReadOnlyDict<K, V>, Cloneable {
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

    public CSharpDictionary(CSharpDictionary<? extends K, ? extends V> other) {
        this.map = new LinkedHashMap<>();
        for (var entry : other.map.entrySet()) {
            this.map.put(entry.getKey(), entry.getValue());
        }
        this.comparer = null;
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

    /** C# ctor accepting a user IEqualityComparer&lt;T&gt; (same shape as the comparer above). */
    public CSharpDictionary(IEqualityComparer<K> comparer) {
        this.map = new LinkedHashMap<>();
        this.comparer = comparer;
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
     * <p>The scan runs only when a comparer was supplied, and returns the key already stored so a
     * later {@code put} replaces its value without changing the key's original spelling — C#
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
        for (K stored : map.keySet()) {
            if (comparer.equals(stored, (K) key)) {
                return stored;
            }
        }
        return null;
    }

    @Override
    public void add(K key, V value) {
        if (storedKey(key) != null) {
            throw new ArgumentException("An element with the same key already exists: " + key);
        }
        map.put(key, value);
    }

    @Override
    public boolean containsKey(K key) {
        if (storedKey(key) != null) {
            return true;
        }
        if (key instanceof Class) {
            Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
            if (normalized != key && map.containsKey((K) normalized)) {
                return true;
            }
            Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
            if (denormalized != key && map.containsKey((K) denormalized)) {
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
            if (key != null && Objects.equals(map.get(key), entry.getValue())) {
                map.remove(key);
                return true;
            }
            return false;
        }
        K key = storedKey(o);
        return key != null && map.remove(key) != null;
    }

    @Override
    @SuppressWarnings("unchecked")
    public V get(K key) {
        K stored = storedKey(key);
        V v = stored == null ? null : map.get(stored);
        if (v == null && key instanceof Class) {
            // Bridge C# type-token classes (CSharpSByte, CSharpByte, etc.)
            // to their Java wrapper equivalents so that dictionary
            // lookups succeed regardless of which form was used as key.
            Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
            if (normalized != key) {
                v = map.get((K) normalized);
            }
            // Also try the reverse: Java wrapper classes (e.g. Byte.class)
            // to C# type-token classes (e.g. CSharpByte.class) so that
            // lookups for Byte.class find converters registered under
            // CSharpByte.class.
            if (v == null) {
                Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
                if (denormalized != key) {
                    v = map.get((K) denormalized);
                }
            }
        }
        return v;
    }

    @Override
    @SuppressWarnings("unchecked")
    public V getChecked(K key) {
        if (!map.containsKey(key)) {
            // Try normalized and denormalized lookups for Class keys, mirroring
            // containsKey()/get() so all read paths bridge type tokens the
            // same way.
            if (key instanceof Class) {
                Object normalized = TypeHelper.normalizeClassToken((Class<?>) key);
                if (normalized != key && map.containsKey((K) normalized)) {
                    return map.get((K) normalized);
                }
                Object denormalized = TypeHelper.denormalizeToCSharpTypeToken((Class<?>) key);
                if (denormalized != key && map.containsKey((K) denormalized)) {
                    return map.get((K) denormalized);
                }
            }
            K stored = storedKey(key);
            if (stored == null) {
                throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
            }
            return map.get(stored);
        }
        return map.get(key);
    }

    @Override
    public V put(K key, V value) {
        // C# keeps the key that is already stored; only the value is replaced.
        K stored = storedKey(key);
        return map.put(stored == null ? key : stored, value);
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

    /** C# Dictionary&lt;int,V&gt;.TryGetValue(key, out int): the IntHolder overload
     * used when the converter lowers an integral out slot to a primitive holder. */
    public boolean tryGetValue(K key, IntHolder result) {
        if (!map.containsKey(key)) {
            return false;
        }
        result.value = ((Number) map.get(key)).intValue();
        return true;
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
        map.put(item.getKey(), item.getValue());
        return true;
    }

    @Override
    public void clear() {
        map.clear();
    }

    public boolean contains(Object o) {
        if (o instanceof Map.Entry<?, ?> entry) {
            K key = storedKey(entry.getKey());
            if (key == null) return false;
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

    public V getOrDefault(K key, V defaultValue) {
        V v = get(key);
        return v == null ? defaultValue : v;
    }

    public CSharpGenericIterableSet<K> keySet() {
        return new CSharpGenericIterableSet<>(map.keySet());
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
        return map.entrySet();
    }

    public boolean tryAdd(K key, V value) {
        if (storedKey(key) != null) {
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
    public Enumerator<K, V> iterator() {
        return new Enumerator<>(map.entrySet().iterator());
    }

    /**
     * Mirrors Dictionary&lt;TKey, TValue&gt;.Enumerator — the C# generic dictionary
     * enumerator. The converter references it as Dictionary.Enumerator (raw).
     *
     * <p>Also implements the non-generic {@link CSharpDictEnumerator} view:
     * generated code that declares IDictionaryEnumerator over a generic
     * dictionary's GetEnumerator binds to this same iterator() (real chain:
     * XmlDocument schema-attribute walks). getCurrent stays the typed pair for
     * generic consumers; the non-generic accessors surface key/value/entry.</p>
     */
    public static class Enumerator<K, V> implements CSharpGenericEnumerator<CSharpKeyValuePair<K, V>>, CSharpDictEnumerator {
        private final Iterator<Map.Entry<K, V>> it;
        private CSharpKeyValuePair<K, V> current;
        private boolean hasCurrent;

        public Enumerator(Iterator<Map.Entry<K, V>> it) {
            this.it = it;
        }

        @Override
        public boolean moveNext() {
            if (!it.hasNext()) {
                current = null;
                hasCurrent = false;
                return false;
            }
            Map.Entry<K, V> e = it.next();
            current = new CSharpKeyValuePair<>(e.getKey(), e.getValue());
            hasCurrent = true;
            return true;
        }

        @Override
        public CSharpKeyValuePair<K, V> getCurrent() {
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current;
        }

        @Override
        public CSharpDictEntry getEntry() {
            return new CSharpDictEntry(getKey(), getValue());
        }

        @Override
        public Object getKey() {
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current.getKey();
        }

        @Override
        public Object getValue() {
            if (!hasCurrent) throw new IllegalStateException("Enumeration has not started or has already finished.");
            return current.getValue();
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

        /**
         * C# Dictionary&lt;TKey,TValue&gt;.Enumerator implements IEnumerator.Reset(),
         * which moves the enumerator back to its initial position (before the first
         * element). The underlying Java Iterator cannot be rewound, so a true reset
         * is impossible. However, in practice reset() is always called immediately
         * after GetEnumerator() (real chain: XmlLoader.loadDocumentType), before
         * any MoveNext(), so the enumerator is still at its initial position and a
         * no-op is semantically correct. Throwing UnsupportedOperationException would
         * break those callers — the C# code calls Reset() defensively before a
         * MoveNext() loop even though the enumerator is already at the start.
         */
        @Override
        public void reset() {
            // no-op: the enumerator is already at the initial position.
        }
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
            @SuppressWarnings("unchecked")
            K key = (K) o;
            return dict.containsKey(key);
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

