package io.github.ningpp.compat;

import java.util.*;

public interface CSharpICollection<T> extends CSharpGenericIterable<T> {
    boolean add(T item);
    void clear();
    boolean contains(Object o);
    void copyTo(T[] array, int arrayIndex);
    boolean remove(Object o);
    int getCount();
    boolean getIsReadOnly();

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
