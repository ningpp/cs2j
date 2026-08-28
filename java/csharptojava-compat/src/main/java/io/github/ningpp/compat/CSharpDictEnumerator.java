package io.github.ningpp.compat;

public interface CSharpDictEnumerator extends CSharpEnumerator {
    CSharpDictEntry getEntry();
    Object getKey();
    Object getValue();

    @Override
    default Object getCurrent() { return getEntry(); }

    /** Iterator-style convenience: hasNext() == moveNext(). */
    default boolean hasNext() { return moveNext(); }

    /** Iterator-style convenience: next() == getCurrent(). */
    default Object next() { return getCurrent(); }

    /**
     * Adapts a generic key/value enumerator into the non-generic IDictionaryEnumerator
     * equivalent used by generated Java code.
     * <p>
     * The enumerator's current element may be either a {@link CSharpKeyValuePair}
     * (from a generic dictionary) or a {@link CSharpDictEntry} (from a non-generic
     * CSharpIDictionary bridge whose iterator returns DictEntry objects). Both
     * are handled at runtime to avoid ClassCastException from generic erasure.
     */
    @SuppressWarnings("unchecked")
    static CSharpDictEnumerator from(CSharpGenericEnumerator<? extends CSharpKeyValuePair<?, ?>> enumerator) {
        return new CSharpDictEnumerator() {
            @Override public boolean moveNext() { return enumerator.moveNext(); }
            @Override public CSharpDictEntry getEntry() {
                Object current = enumerator.getCurrent();
                if (current instanceof CSharpDictEntry entry) {
                    return entry;
                }
                if (current instanceof java.util.Map.Entry<?, ?> entry) {
                    return new CSharpDictEntry(entry.getKey(), entry.getValue());
                }
                throw new IllegalStateException("Expected dictionary entry but got " + (current == null ? "null" : current.getClass().getName()));
            }
            @Override public Object getKey() { return getEntry().getKey(); }
            @Override public Object getValue() { return getEntry().getValue(); }
        };
    }

    /**
     * Adapts a non-generic enumerator whose current element is a dictionary entry
     * (e.g. from Hashtable/SortedList) into a CSharpDictEnumerator.
     */
    static CSharpDictEnumerator from(CSharpEnumerator enumerator) {
        return new CSharpDictEnumerator() {
            @Override public boolean moveNext() { return enumerator.moveNext(); }
            @Override public CSharpDictEntry getEntry() {
                Object current = enumerator.getCurrent();
                if (current instanceof CSharpDictEntry entry) {
                    return entry;
                }
                if (current instanceof java.util.Map.Entry<?, ?> entry) {
                    return new CSharpDictEntry(entry.getKey(), entry.getValue());
                }
                throw new IllegalStateException("Expected dictionary entry but got " + current.getClass().getName());
            }
            @Override public Object getKey() { return getEntry().getKey(); }
            @Override public Object getValue() { return getEntry().getValue(); }
        };
    }
}
