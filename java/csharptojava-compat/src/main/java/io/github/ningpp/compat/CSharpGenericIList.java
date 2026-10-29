package io.github.ningpp.compat;

public interface CSharpGenericIList<T> extends CSharpICollection<T> {
    int indexOf(Object o);
    void insert(int index, T item);
    void removeAt(int index);
    T get(int index);
    T set(int index, T value);

    /** C# IList.IsFixedSize — non-generic lists report false; generic lists have no such member. */
    default boolean getIsFixedSize() {
        return false;
    }

    default T reduce(T identity, java.util.function.BinaryOperator<T> operator) {
        T result = identity;
        for (int i = 0; i < getCount(); i++) {
            result = operator.apply(result, get(i));
        }
        return result;
    }

    /**
     * Adapts a concrete generic list (CSharpList&lt;T&gt; / CSharpGenericIList&lt;T&gt;)
     * or a non-generic CSharpIList / CSharpArrayList to CSharpGenericIList&lt;Object&gt;.
     * Used by the converter when the declared C# type is System.Collections.IList
     * (which maps to CSharpGenericIList&lt;Object&gt;) but the expression's Java static
     * type is a more specific generic list that Java's invariant generics reject.
     */
    @SuppressWarnings("unchecked")
    static CSharpGenericIList<Object> from(Object list) {
        if (list == null) {
            return null;
        }
        if (list instanceof CSharpGenericIList) {
            return (CSharpGenericIList<Object>) list;
        }
        if (list instanceof CSharpIList) {
            return fromCSharpIList((CSharpIList) list);
        }
        if (list instanceof CSharpReadOnlyDict) {
            return fromCSharpReadOnlyDict((CSharpReadOnlyDict) list);
        }
        throw new IllegalArgumentException("Unsupported list type: " + list.getClass().getName());
    }

    /**
     * Adapts a CSharpReadOnlyDict-backed collection (e.g. GroupCollection) to the
     * non-generic IList view. The C# non-generic IList enumeration yields the
     * dictionary's VALUES, and the collection is read-only.
     */
    @SuppressWarnings("unchecked")
    static CSharpGenericIList<Object> fromCSharpReadOnlyDict(CSharpReadOnlyDict dict) {
        CSharpList<Object> values = new CSharpList<>();
        for (Object value : dict.values()) {
            values.add(value);
        }
        return new CSharpGenericIList<Object>() {
            @Override public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.from(values.iterator());
            }
            @Override public int getCount() { return values.size(); }
            @Override public boolean getIsReadOnly() { return true; }
            @Override public boolean getIsFixedSize() { return true; }
            @Override public void copyTo(Object[] array, int arrayIndex) {
                values.copyTo(array, arrayIndex);
            }
            @Override public boolean add(Object item) { throw new UnsupportedOperationException(); }
            @Override public void clear() { throw new UnsupportedOperationException(); }
            @Override public boolean contains(Object o) { return values.contains(o); }
            @Override public boolean remove(Object o) { throw new UnsupportedOperationException(); }
            @Override public int indexOf(Object o) { return values.indexOf(o); }
            @Override public void insert(int index, Object item) { throw new UnsupportedOperationException(); }
            @Override public void removeAt(int index) { throw new UnsupportedOperationException(); }
            @Override public Object get(int index) { return values.get(index); }
            @Override public Object set(int index, Object value) { throw new UnsupportedOperationException(); }
        };
    }

    /**
     * Typed version of {@link #from(Object)} that preserves the element type.
     * Returns the same CSharpGenericIList if the input already implements it.
     * Used by the converter when the target C# type is IList&lt;T&gt;
     * (not the non-generic IList).
     */
    @SuppressWarnings("unchecked")
    static <T> CSharpGenericIList<T> fromTyped(Object list) {
        if (list == null) {
            return null;
        }
        if (list instanceof CSharpGenericIList) {
            return (CSharpGenericIList<T>) list;
        }
        if (list instanceof CSharpIList) {
            return (CSharpGenericIList<T>) fromCSharpIList((CSharpIList) list);
        }
        throw new IllegalArgumentException("Unsupported list type: " + list.getClass().getName());
    }

    static CSharpGenericIList<Object> fromCSharpIList(CSharpIList list) {
        return new CSharpGenericIList<Object>() {
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.from(list.iterator());
            }

            @Override
            public int getCount() {
                return list.getCount();
            }

            @Override
            public void copyTo(Object[] array, int arrayIndex) {
                list.copyTo(array, arrayIndex);
            }

            @Override
            public boolean add(Object item) {
                list.add(item);
                return true;
            }

            @Override
            public void clear() {
                list.clear();
            }

            @Override
            public boolean contains(Object o) {
                return list.contains(o);
            }

            @Override
            public boolean remove(Object o) {
                list.remove(o);
                return true;
            }

            @Override
            public int indexOf(Object o) {
                return list.indexOf(o);
            }

            @Override
            public void insert(int index, Object item) {
                list.insert(index, item);
            }

            @Override
            public void removeAt(int index) {
                list.removeAt(index);
            }

            @Override
            public Object get(int index) {
                return list.get(index);
            }

            @Override
            public Object set(int index, Object value) {
                return list.set(index, value);
            }
        };
    }
}
