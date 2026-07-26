package io.github.ningpp.compat;

import java.util.*;

public interface CSharpICollection<T> extends CSharpGenericIterable<T> {
    /**
     * Adapts a CSharpCollection (non-generic ICollection) or passes through an existing
     * CSharpICollection as CSharpICollection<Object>. Used by the converter when the
     * declared C# type is System.Collections.ICollection but the expression's Java static
     * type is CSharpCollection, which is not assignable to CSharpICollection<?>.
     */
    static CSharpICollection<Object> from(Object collection) {
        if (collection == null) {
            return null;
        }
        if (collection instanceof CSharpICollection) {
            @SuppressWarnings("unchecked")
            CSharpICollection<Object> result = (CSharpICollection<Object>) collection;
            return result;
        }
        if (collection instanceof CSharpCollection) {
            return fromCSharpCollection((CSharpCollection) collection);
        }
        if (collection instanceof java.util.Collection) {
            return fromJavaCollection((java.util.Collection<?>) collection);
        }
        throw new IllegalArgumentException("Unsupported collection type: " + collection.getClass().getName());
    }

    @SuppressWarnings("unchecked")
    static CSharpICollection<Object> fromJavaCollection(java.util.Collection<?> collection) {
        java.util.Collection<Object> backing = (java.util.Collection<Object>) collection;
        return new CSharpICollection<Object>() {
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.from(collection.iterator());
            }

            @Override
            public int getCount() {
                return collection.size();
            }

            @Override
            public boolean contains(Object o) {
                return collection.contains(o);
            }

            @Override
            public boolean add(Object item) {
                return backing.add(item);
            }

            @Override
            public boolean remove(Object o) {
                return collection.remove(o);
            }

            @Override
            public void clear() {
                collection.clear();
            }
        };
    }

    /**
     * Typed version of {@link #from(Object)} that preserves the element type.
     * Returns the same CSharpICollection if the input already implements it,
     * otherwise wraps a Java Collection with the correct generic type.
     * Used by the converter when the target C# type is ICollection&lt;T&gt;
     * (not the non-generic ICollection).
     */
    @SuppressWarnings("unchecked")
    static <T> CSharpICollection<T> fromTyped(Object collection) {
        if (collection == null) {
            return null;
        }
        if (collection instanceof CSharpICollection) {
            return (CSharpICollection<T>) collection;
        }
        if (collection instanceof CSharpCollection) {
            return (CSharpICollection<T>) fromCSharpCollection((CSharpCollection) collection);
        }
        if (collection instanceof java.util.Collection) {
            java.util.Collection<T> backing = (java.util.Collection<T>) collection;
            return new CSharpICollection<T>() {
                @Override
                public CSharpGenericEnumerator<T> iterator() {
                    return CSharpGenericEnumerator.from(backing.iterator());
                }

                @Override
                public int getCount() {
                    return backing.size();
                }

                @Override
                public boolean contains(Object o) {
                    return backing.contains(o);
                }

                @Override
                public boolean add(T item) {
                    return backing.add(item);
                }

                @Override
                public boolean remove(Object o) {
                    return backing.remove(o);
                }

                @Override
                public void clear() {
                    backing.clear();
                }
            };
        }
        throw new IllegalArgumentException("Unsupported collection type: " + collection.getClass().getName());
    }

    static CSharpICollection<Object> fromCSharpCollection(CSharpCollection collection) {
        return new CSharpICollection<Object>() {
            @Override public CSharpGenericEnumerator<Object> iterator() {
                return collection.iterator();
            }
            @Override public int getCount() {
                return collection.getCount();
            }
            @Override public void copyTo(Object[] array, int arrayIndex) {
                collection.copyTo(array, arrayIndex);
            }
            @Override public void copyTo(CSharpArray array, int index) {
                collection.copyTo(array, index);
            }
            @Override public boolean getIsSynchronized() {
                return collection.getIsSynchronized();
            }
            @Override public Object getSyncRoot() {
                return collection.getSyncRoot();
            }
        };
    }

    default boolean add(T item) { throw new UnsupportedOperationException(); }
    default void clear() { throw new UnsupportedOperationException(); }
    default boolean contains(Object o) { throw new UnsupportedOperationException(); }
    default void copyTo(T[] array, int arrayIndex) { throw new UnsupportedOperationException(); }
    default boolean remove(Object o) { throw new UnsupportedOperationException(); }
    int getCount();
    default boolean getIsReadOnly() { return false; }

    // CSharpCollection-compatible methods (non-generic ICollection surface)
    default void copyTo(CSharpArray array, int index) {
        int i = 0;
        for (T elem : this) {
            array.setValue(elem, index + i);
            i++;
        }
    }

    default boolean getIsSynchronized() { return false; }
    default Object getSyncRoot() { return this; }

    @Override
    default int size() {
        return getCount();
    }

    @Override
    default boolean isEmpty() {
        return getCount() == 0;
    }

    @Override
    default boolean addAll(Collection<? extends T> c) {
        boolean modified = false;
        for (T e : c) {
            if (add(e)) modified = true;
        }
        return modified;
    }

    @Override
    default boolean removeAll(Collection<?> c) {
        boolean modified = false;
        for (Object e : c) {
            if (remove(e)) modified = true;
        }
        return modified;
    }

    @Override
    default boolean retainAll(Collection<?> c) {
        boolean modified = false;
        Iterator<T> it = iterator();
        while (it.hasNext()) {
            if (!c.contains(it.next())) {
                it.remove();
                modified = true;
            }
        }
        return modified;
    }

    @Override
    default Object[] toArray() {
        Object[] arr = new Object[getCount()];
        int i = 0;
        for (T e : this) arr[i++] = e;
        return arr;
    }

    @Override
    @SuppressWarnings("unchecked")
    default <E> E[] toArray(E[] a) {
        int size = getCount();
        if (a.length < size) {
            return (E[]) Arrays.copyOf(toArray(), size, a.getClass());
        }
        int i = 0;
        for (T e : this) a[i++] = (E) e;
        if (a.length > size) a[size] = null;
        return a;
    }
}
