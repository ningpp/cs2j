package io.github.ningpp.compat;

import java.util.*;
import java.util.stream.*;
import java.util.function.*;

public interface CSharpGenericIterable<T> extends Collection<T> {
    @Override
    CSharpGenericEnumerator<T> iterator();

    /**
     * The original value behind a {@code from*}/{@code asIterable} adapter; the
     * identity default keeps generated implementors (iterators, views)
     * source-compatible while adapters override it with the wrapped value.
     */
    default Object unwrap() {
        return this;
    }

    /**
     * The original value behind an adapter created by this interface (or
     * {@code value} itself when it is not an adapter), so a C# reference
     * downcast out of the IEnumerable world can be rendered as a plain Java
     * cast against the original object.
     */
    static Object unwrap(Object value) {
        if (value instanceof CSharpGenericIterable<?> iterable) {
            return iterable.unwrap();
        }
        return value;
    }

    /** C# {@code value as T}: unwraps an adapter, then checks the runtime type. */
    @SuppressWarnings("unchecked")
    static <T> T tryAs(Object value, Class<T> type) {
        Object unwrapped = unwrap(value);
        return type.isInstance(unwrapped) ? (T) unwrapped : null;
    }

    /**
     * C# Enumerable.FirstOrDefault(): the first element or null. A default
     * method (rather than the java.util.stream findFirst() Optional shape) so a
     * generated IEnumerable-typed slot chain (Where...FirstOrDefault) resolves
     * with the C# null-on-empty contract on the erased iterable carrier
     * (real chain: ExtensionMethods.filterMethodBases).
     */
    default T findFirst() {
        Iterator<T> it = iterator();
        return it.hasNext() ? it.next() : null;
    }

    /**
     * C# Enumerable.Any() without a predicate: true when the sequence has at
     * least one element. The method mapping lowers {@code xs.Any()} to
     * {@code anyMatch()}, but the java.util.stream shape requires a Predicate;
     * this no-arg default keeps the C# contract on the erased iterable carrier
     * (real chain: AGL tools/ArgsParser UsageString).
     */
    default boolean anyMatch() {
        return iterator().hasNext();
    }

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
            @Override
            public Object unwrap() {
                return iterable;
            }
        };
    }

    /**
     * Bridge from the CSharpIList/CSharpCollection world into the generic-iterable
     * world. Those interfaces erase to {@code CSharpIterable<Object>}, so
     * {@code from(Iterable<? extends T>)} cannot prove element compatibility even
     * when the concrete collection is element-typed (GroupCollection implements
     * CSharpIList&lt;Group&gt;). Enumerable.Cast&lt;T&gt;() rewrites target this
     * overload with an explicit type witness; elements are cast lazily, matching
     * C#'s Cast enumerator.
     */
    static <T> CSharpGenericIterable<T> from(CSharpIterable<?> iterable) {
        if (iterable == null) {
            return null;
        }
        java.util.List<T> items = new java.util.ArrayList<>();
        for (Object item : iterable) {
            @SuppressWarnings("unchecked")
            T cast = (T) item;
            items.add(cast);
        }
        return fromCollection(items);
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
        return new CSharpGenericIterable<T>() {
            @Override
            public Object unwrap() {
                return collection;
            }
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
    static CSharpGenericIterable fromAny(Object value) {        if (value == null) {
            return null;
        }
        if (value instanceof CSharpGenericIterable) {
            return (CSharpGenericIterable) value;
        }
        if (value instanceof Iterable) {
            return from((Iterable<?>) value);
        }
        if (value instanceof java.util.stream.Stream<?> stream) {
            Object[] elements = stream.toArray();
            return fromAny(elements);
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
                @Override public Object unwrap() { return value; }
            };
        }
        throw new UnsupportedOperationException(
            "Cannot convert " + value.getClass() + " to CSharpGenericIterable");
    }

    /** Typed view of {@link #fromAny(Object)} for converter-emitted adapters. */
    @SuppressWarnings("unchecked")
    static <T> CSharpGenericIterable<T> fromAnyTyped(Object value) {
        return (CSharpGenericIterable<T>) fromAny(value);
    }

    /** C# Enumerable.FirstOrDefault(predicate) — null when no element matches. */
    default T findAny(java.util.function.Predicate<T> predicate) {
        for (T element : this) {
            if (predicate.test(element)) {
                return element;
            }
        }
        return null;
    }

    /** C# Enumerable.FirstOrDefault() — null when empty. */
    default T findAny() {
        java.util.Iterator<T> iterator = iterator();
        return iterator.hasNext() ? iterator.next() : null;
    }

    /** C# Enumerable.Cast&lt;T&gt;() — element casts are inserted at the use site. */
    @SuppressWarnings("unchecked")
    default <U> CSharpGenericIterable<U> cast() {
        return (CSharpGenericIterable<U>) this;
    }

    /**
     * OrderBy/ThenBy on an Iterable-typed receiver (e.g. the result of a
     * procedural-LINQ helper) map to {@code sorted(Comparator)}; the default
     * Iterable/Collection surface has no such method, so materialize a sorted
     * view. Successive stable sorts compose multi-key ordering like C#.
     */
    default CSharpGenericIterable<T> sorted(java.util.Comparator<? super T> comparator) {
        java.util.List<T> items = new java.util.ArrayList<>();
        for (T item : this) {
            items.add(item);
        }
        items.sort(comparator);
        return CSharpGenericIterable.fromCollection(items);
    }

    /** Materialize a residual LINQ stream terminal on the compat carrier. */
    default <R, A> R collect(java.util.stream.Collector<? super T, A, R> collector) {
        return stream().collect(collector);
    }

    /** C# Enumerable.GroupBy(keySelector): materialized grouping. */
    default <K> CSharpDictionary<K, CSharpList<T>> collect(
            java.util.function.Function<? super T, ? extends K> keySelector) {
        CSharpDictionary<K, CSharpList<T>> result = new CSharpDictionary<>();
        for (T element : this) {
            K key = keySelector.apply(element);
            CSharpList<T> bucket = result.get(key);
            if (bucket == null) {
                bucket = new CSharpList<>();
                result.put(key, bucket);
            }
            bucket.add(element);
        }
        return result;
    }

    /** C# Enumerable.Reverse(): a new reversed sequence (List.Reverse() is a
     * separate in-place void method on CSharpList, hence the distinct name). */
    default CSharpGenericIterable<T> reverseSequence() {
        java.util.List<T> items = new java.util.ArrayList<>();
        for (T item : this) {
            items.add(item);
        }
        java.util.Collections.reverse(items);
        return CSharpGenericIterable.fromCollection(items);
    }

    /** C# Enumerable.Any(predicate). */
    default boolean anyMatch(java.util.function.Predicate<? super T> predicate) {
        for (T item : this) {
            if (predicate.test(item)) {
                return true;
            }
        }
        return false;
    }

    /** Materialize a residual Java Stream into the C# IEnumerable carrier. */
    static <T> CSharpGenericIterable<T> from(java.util.stream.Stream<? extends T> stream) {
        return fromCollection(stream.collect(java.util.stream.Collectors.toList()));
    }

    /**
     * Dictionary-as-IEnumerable bridge: C# Dictionary implements
     * IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;, but the compat dictionary
     * deliberately stays outside Collection to avoid add(Object) erasure
     * clashes. Adapter used where a generated IEnumerable parameter receives a
     * dictionary value.
     */
    static <K, V> CSharpGenericIterable<CSharpKeyValuePair<K, V>> fromEntries(
            CSharpGenericIDictionary<K, V> dictionary) {
        if (dictionary == null) {
            return null;
        }
        @SuppressWarnings("unchecked")
        CSharpIterable<CSharpKeyValuePair<K, V>> entries =
                (CSharpIterable<CSharpKeyValuePair<K, V>>) (CSharpIterable<?>) dictionary;
        return from(entries);
    }

    /** C# List&lt;T&gt;.ToArray(): typed materialization distinct from Collection.toArray. */
    default <E> E[] toArrayCs(E[] array) {
        java.util.List<E> items = new java.util.ArrayList<>();
        for (T item : this) {
            @SuppressWarnings("unchecked")
            E cast = (E) item;
            items.add(cast);
        }
        return items.toArray(array);
    }

    /** C# List&lt;T&gt;.ToArray() without an explicit array; E is inferred from the slot. */
    @SuppressWarnings("unchecked")
    default <E> E[] toArrayCs() {
        return (E[]) toArray();
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
