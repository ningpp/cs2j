package io.github.ningpp.compat;

import java.util.*;

public interface CSharpICollection<T> extends CSharpGenericIterable<T> {
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
