package io.github.ningpp.compat;

public interface CSharpDictEnumerator extends CSharpEnumerator {
    CSharpDictEntry getEntry();
    Object getKey();
    Object getValue();

    @Override
    default Object getCurrent() { return getEntry(); }

    @Override
    default boolean hasNext() { return moveNext(); }

    @Override
    default Object next() { return getCurrent(); }

    /**
     * Adapts a generic key/value enumerator into the non-generic IDictionaryEnumerator
     * equivalent used by generated Java code.
     */
    static CSharpDictEnumerator from(CSharpGenericEnumerator<? extends CSharpKeyValuePair<?, ?>> enumerator) {
        return new CSharpDictEnumerator() {
            @Override public boolean moveNext() { return enumerator.moveNext(); }
            @Override public CSharpDictEntry getEntry() {
                CSharpKeyValuePair<?, ?> pair = enumerator.getCurrent();
                return new CSharpDictEntry(pair.getKey(), pair.getValue());
            }
            @Override public Object getKey() { return enumerator.getCurrent().getKey(); }
            @Override public Object getValue() { return enumerator.getCurrent().getValue(); }
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
