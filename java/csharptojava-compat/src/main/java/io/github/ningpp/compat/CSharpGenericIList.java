package io.github.ningpp.compat;

public interface CSharpGenericIList<T> extends CSharpICollection<T> {
    int indexOf(Object o);
    void insert(int index, T item);
    void removeAt(int index);
    T get(int index);
    T set(int index, T value);

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
        throw new IllegalArgumentException("Unsupported list type: " + list.getClass().getName());
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
