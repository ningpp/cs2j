package io.github.ningpp.compat;

/**
 * C# IReadOnlyDictionary&lt;K,V&gt; compat view. Deliberately does not extend
 * CSharpReadOnlyCollection: a generated dictionary collection also implements
 * CSharpIList (whose Collection&lt;Object&gt; base declares add(Object)), and
 * inheriting Collection&lt;KeyValuePair&gt; from this interface would make the
 * class inherit the same erased add with two different type arguments.
 */
public interface CSharpReadOnlyDict<K, V> {
    int getCount();
    CSharpGenericEnumerator iterator();
    boolean containsKey(K key);
    V get(K key);

    /**
     * C# IReadOnlyDictionary&lt;K,V&gt;.Keys. C# requires every IReadOnlyDictionary
     * implementation to expose Keys, and documents that Keys enumerates in the
     * same order as GetEnumerator — the invariant {@link #pairIterator()} relies on.
     * Generated dictionary classes emit this from the C# Keys property; compat
     * dictionaries already declare it.
     */
    CSharpGenericIterable<K> getKeys();

    /** C# IReadOnlyDictionary&lt;K,V&gt;.Values — same ordering contract as {@link #getKeys()}. */
    CSharpGenericIterable<V> getValues();

    /**
     * C# IEnumerator&lt;KeyValuePair&lt;K,V&gt;&gt; view — the GetEnumerator overload the
     * compiler selects when the receiver's static type is IReadOnlyDictionary&lt;K,V&gt;,
     * IDictionary&lt;K,V&gt; or IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;. Dual-view generated
     * collections (e.g. Regex GroupCollection) expose their VALUE enumerator through
     * {@link #iterator()} — matching the C# public GetEnumerator — so the entry view
     * cannot reuse it: it zips the order-aligned Keys/Values iterables instead,
     * matching the documented C# contract that both enumerate in GetEnumerator order.
     */
    default CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> pairIterator() {
        return new PairIterator<>(getKeys(), getValues());
    }

    /**
     * Mirrors IReadOnlyDictionary&lt;TKey, TValue&gt;.get_Item: throws
     * KeyNotFoundException when the key is absent. Emitted for C# indexer reads.
     */
    default V getChecked(K key) {
        if (!containsKey(key)) {
            throw new KeyNotFoundException("The given key '" + key + "' was not present in the dictionary.");
        }
        return get(key);
    }

    default CSharpGenericIterable<K> keySet() {
        CSharpList<K> keys = new CSharpList<>();
        CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> e = pairIterator();
        while (e.moveNext()) {
            keys.add(e.getCurrent().getKey());
        }
        return keys;
    }

    default CSharpGenericIterable<V> values() {
        CSharpList<V> vals = new CSharpList<>();
        CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> e = pairIterator();
        while (e.moveNext()) {
            vals.add(e.getCurrent().getValue());
        }
        return vals;
    }

    default V tryGetValue(K key) {
        return get(key);
    }

    /**
     * Zips the order-aligned Keys/Values iterables into the C#
     * IEnumerator&lt;KeyValuePair&lt;K,V&gt;&gt; protocol: Current before the first MoveNext
     * or after the end throws InvalidOperationException (mapped to
     * IllegalStateException), and Reset reacquires fresh underlying enumerators
     * (compiler-generated iterator classes disallow Reset on themselves).
     */
    final class PairIterator<K, V> implements CSharpGenericEnumerator<CSharpKeyValuePair<K, V>> {
        private static final int BEFORE_START = -1;
        private static final int RUNNING = 0;
        private static final int ENDED = 1;

        private final CSharpGenericIterable<K> keys;
        private final CSharpGenericIterable<V> values;
        private CSharpGenericEnumerator<K> keyEnumerator;
        private CSharpGenericEnumerator<V> valueEnumerator;
        private CSharpKeyValuePair<K, V> current;
        private int state = BEFORE_START;

        public PairIterator(CSharpGenericIterable<K> keys, CSharpGenericIterable<V> values) {
            this.keys = keys;
            this.values = values;
        }

        @Override
        public boolean moveNext() {
            if (state == ENDED) {
                return false;
            }
            if (keyEnumerator == null) {
                keyEnumerator = keys.iterator();
                valueEnumerator = values.iterator();
            }
            boolean hasKey = keyEnumerator.moveNext();
            boolean hasValue = valueEnumerator.moveNext();
            if (!hasKey || !hasValue) {
                state = ENDED;
                current = null;
                return false;
            }
            state = RUNNING;
            current = new CSharpKeyValuePair<>(keyEnumerator.getCurrent(), valueEnumerator.getCurrent());
            return true;
        }

        @Override
        public CSharpKeyValuePair<K, V> getCurrent() {
            if (state != RUNNING) {
                throw new IllegalStateException("Enumeration has not started or has already finished.");
            }
            return current;
        }

        @Override
        public void reset() {
            keyEnumerator = null;
            valueEnumerator = null;
            current = null;
            state = BEFORE_START;
        }
    }
}
