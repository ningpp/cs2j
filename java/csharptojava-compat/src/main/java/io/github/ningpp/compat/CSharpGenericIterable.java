package io.github.ningpp.compat;

import java.util.*;
import java.util.stream.*;
import java.util.function.*;

public interface CSharpGenericIterable<T> extends Collection<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();

    static <T> CSharpGenericIterable<T> from(Iterable<? extends T> iterable) {
        // C# IEnumerable<T> is a reference type: a null source must stay null.
        // Wrapping null in an anonymous CSharpGenericIterable would create a
        // "non-null object whose inner iterable is null", silently destroying
        // C# null checks such as `if (path.PathPoints == null)`.
        if (iterable == null) {
            return null;
        }
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

    /** C# string is IEnumerable<char> — LINQ rewrites call Select/Where on strings. */
    static CSharpGenericIterable<Character> from(String s) {
        // A null C# string is a null IEnumerable<char>, not an empty one.
        if (s == null) {
            return null;
        }
        java.util.ArrayList<Character> chars = new java.util.ArrayList<>(s.length());
        for (int i = 0; i < s.length(); i++) {
            chars.add(s.charAt(i));
        }
        return fromCollection(chars);
    }

    @SuppressWarnings("unchecked")
    /** Mirrors C# Enumerable methods: LINQ sources must not be null (ArgumentNullException). */
    static <T> T requireNonNull(T value, String paramName) {
        if (value == null) {
            throw new ArgumentNullException(paramName);
        }
        return value;
    }

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

    /**
     * Handles C# 'where T : IEnumerable' constraint erased at compile time.
     * In .NET, arrays implement IEnumerable, but Java arrays do not implement
     * Iterable.  This method bridges the gap by accepting either an Iterable
     * or an array and returning a CSharpGenericIterable view, so generated code
     * like {@code CSharpGenericIterable.fromAny(value).iterator()} works
     * regardless of whether value is a CSharpList, a plain Collection, or an
     * array (e.g. int[][], String[], Object[]).
     */
    @SuppressWarnings({"unchecked", "rawtypes"})
    static CSharpGenericIterable fromAny(Object value) {
        if (value == null) {
            return null;
        }
        if (value instanceof CSharpGenericIterable) {
            return (CSharpGenericIterable) value;
        }
        if (value instanceof Iterable) {
            return from((Iterable<?>) value);
        }
        if (value.getClass().isArray()) {
            return new CSharpGenericIterable() {
                @Override
                public CSharpGenericEnumerator iterator() {
                    // Use reflection to iterate any array (primitive or reference).
                    int len = java.lang.reflect.Array.getLength(value);
                    List list = new ArrayList<>(len);
                    for (int i = 0; i < len; i++) {
                        list.add(java.lang.reflect.Array.get(value, i));
                    }
                    return CSharpGenericEnumerator.from(list.iterator());
                }
                @Override public int size() { return java.lang.reflect.Array.getLength(value); }
            };
        }
        throw new UnsupportedOperationException(
            "Cannot convert " + value.getClass() + " to CSharpGenericIterable");
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
