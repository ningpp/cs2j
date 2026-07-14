package io.github.ningpp.compat;

import java.util.*;
import java.util.stream.*;
import java.util.function.*;

public interface CSharpGenericIterable<T> extends Collection<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();

    static <T> CSharpGenericIterable<T> from(Iterable<? extends T> iterable) {
        if (iterable instanceof CSharpGenericIterable) {
            @SuppressWarnings("unchecked")
            CSharpGenericIterable<T> result = (CSharpGenericIterable<T>) iterable;
            return result;
        }
        if (iterable instanceof Collection) {
            @SuppressWarnings("unchecked")
            Collection<? extends T> coll = (Collection<? extends T>) iterable;
            return fromCollection(coll);
        }
        return new CSharpGenericIterable<>() {
            @Override
            public CSharpGenericEnumerator<T> iterator() {
                return CSharpGenericEnumerator.from(iterable.iterator());
            }
        };
    }

    @SuppressWarnings("unchecked")
    static <T> CSharpGenericIterable<T> fromCollection(Collection<? extends T> collection) {
        return new CSharpGenericIterable<>() {
            @Override
            public CSharpGenericEnumerator<T> iterator() {
                return CSharpGenericEnumerator.from(collection.iterator());
            }
            @Override public int size() { return collection.size(); }
            @Override public boolean isEmpty() { return collection.isEmpty(); }
            @Override public boolean contains(Object o) { return collection.contains(o); }
            @Override public Object[] toArray() { return collection.toArray(); }
            @Override public <E> E[] toArray(E[] a) { return collection.toArray(a); }
            @Override public boolean add(T e) { throw new UnsupportedOperationException(); }
            @Override public boolean remove(Object o) { return collection.remove(o); }
            @Override public boolean containsAll(Collection<?> c) { return collection.containsAll(c); }
            @Override public boolean addAll(Collection<? extends T> c) { throw new UnsupportedOperationException(); }
            @Override public boolean removeAll(Collection<?> c) { return collection.removeAll(c); }
            @Override public boolean retainAll(Collection<?> c) { return collection.retainAll(c); }
            @Override public void clear() { collection.clear(); }
        };
    }

    // Default Collection implementations for lazy iterables
    @Override
    default int size() {
        int count = 0;
        for (T ignored : this) count++;
        return count;
    }

    @Override
    default boolean isEmpty() {
        return !iterator().hasNext();
    }

    @Override
    default boolean contains(Object o) {
        for (T e : this) {
            if (Objects.equals(e, o)) return true;
        }
        return false;
    }

    @Override
    default Object[] toArray() {
        List<T> list = new ArrayList<>();
        for (T e : this) list.add(e);
        return list.toArray();
    }

    @Override
    default <E> E[] toArray(E[] a) {
        List<T> list = new ArrayList<>();
        for (T e : this) list.add(e);
        return list.toArray(a);
    }

    @Override
    default boolean add(T e) {
        throw new UnsupportedOperationException();
    }

    @Override
    default boolean remove(Object o) {
        throw new UnsupportedOperationException();
    }

    @Override
    default boolean containsAll(Collection<?> c) {
        for (Object e : c) {
            if (!contains(e)) return false;
        }
        return true;
    }

    @Override
    default boolean addAll(Collection<? extends T> c) {
        throw new UnsupportedOperationException();
    }

    @Override
    default boolean removeAll(Collection<?> c) {
        throw new UnsupportedOperationException();
    }

    @Override
    default boolean retainAll(Collection<?> c) {
        throw new UnsupportedOperationException();
    }

    @Override
    default void clear() {
        throw new UnsupportedOperationException();
    }
}
