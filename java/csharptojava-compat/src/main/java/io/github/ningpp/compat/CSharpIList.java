package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.function.Function;
import java.util.function.IntFunction;
import java.util.function.Predicate;
import java.util.stream.Collector;
import java.util.stream.Collectors;

/**
 * C# non-generic IList compat view.
 *
 * <p>The C# generic <c>IList&lt;T&gt;</c>/<c>ICollection&lt;T&gt;</c> interfaces are
 * also mapped here. The type parameter is intentionally unused: all members keep
 * Object parameters, so a generated read-only collection that also declares the
 * C# generic (T-parameterized) explicit implementations does not hit Java's
 * "same erasure, neither overrides" clash between the generic and non-generic
 * flavours. Element accessors stay covariant (a generated <c>Match get(int)</c>
 * overrides <c>Object get(int)</c>).</p>
 *
 * <p>The C# non-generic <c>System.Collections.ICollection</c> maps here too, and
 * it only requires Count/CopyTo/IsSynchronized/SyncRoot. Generated views backed
 * by it therefore implement just the collection surface, so every pure IList
 * member carries a default: mutators throw UnsupportedOperationException (C#
 * throws NotSupportedException for a read-only view), and IsFixedSize/IsReadOnly
 * report false. removeAt returns Object rather than void so a generated C#
 * RemoveAt that yields the removed item (e.g. XmlAttributeCollection.RemoveAt
 * returning XmlAttribute) overrides it covariantly instead of clashing with an
 * incompatible return type.</p>
 */
public interface CSharpIList<T> extends CSharpCollection {
    /**
     * C# ICollection.CopyTo / Enumerable.ToArray materialization for the
     * non-generic IList view. The erased hierarchy cannot extend Collection, so
     * generated List&lt;T&gt;.ToArray() calls would otherwise find no overload.
     */
    default Object[] toArray() {
        ArrayList<Object> items = new ArrayList<>();
        for (Object item : this) {
            items.add(item);
        }
        return items.toArray();
    }

    default <E> E[] toArray(IntFunction<E[]> generator) {
        ArrayList<Object> items = new ArrayList<>();
        for (Object item : this) {
            items.add(item);
        }
        return items.toArray(generator.apply(items.size()));
    }

    /** C# List&lt;T&gt;.ToArray(): distinct name because the erased IList family
     * cannot declare the same generic erasure as CSharpArrayList.toArray(T[]). */
    default <E> E[] toArrayCs(E[] array) {
        ArrayList<Object> items = new ArrayList<>();
        for (Object item : this) {
            items.add(item);
        }
        return items.toArray(array);
    }

    /** Zero-argument List&lt;T&gt;.ToArray(); E is inferred from the assignment slot. */
    @SuppressWarnings("unchecked")
    default <E> E[] toArrayCs() {
        return (E[]) toArray();
    }

    /** C# Enumerable.ToList()/ToList on an IList-typed receiver. */
    @SuppressWarnings("unchecked")
    default CSharpList<T> toList() {
        CSharpList<T> result = new CSharpList<>();
        for (Object item : this) {
            result.add((T) item);
        }
        return result;
    }

    /** C# Enumerable.OrderBy(): materialized stable sort. */
    @SuppressWarnings("unchecked")
    default CSharpGenericIterable<T> sorted(Comparator<? super T> comparator) {
        CSharpList<T> items = toList();
        items.sort(comparator);
        return CSharpGenericIterable.fromCollection(items);
    }

    /** Materialize a residual LINQ stream terminal on an IList-typed receiver. */
    default T reduce(T identity, java.util.function.BinaryOperator<T> operator) {
        T result = identity;
        for (T item : toList()) {
            result = operator.apply(result, item);
        }
        return result;
    }

    default CSharpGenericIterable<T> except(CSharpGenericIterable<T> second) {
        java.util.Set<T> excluded = new java.util.HashSet<>();
        if (second != null) {
            for (T item : second) {
                excluded.add(item);
            }
        }
        java.util.List<T> result = new java.util.ArrayList<>();
        java.util.Set<T> seen = new java.util.HashSet<>();
        for (T item : toList()) {
            if (!excluded.contains(item) && seen.add(item)) {
                result.add(item);
            }
        }
        return CSharpGenericIterable.fromCollection(result);
    }

    default <R, A> R collect(Collector<? super T, A, R> collector) {
        return toList().stream().collect(collector);
    }

    /** C# Enumerable.GroupBy(keySelector): materialized grouping. */
    @SuppressWarnings("unchecked")
    default <K> CSharpDictionary<K, CSharpList<T>> collect(Function<? super T, ? extends K> keySelector) {
        CSharpDictionary<K, CSharpList<T>> result = new CSharpDictionary<>();
        for (Object item : this) {
            T element = (T) item;
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

    /** C# Enumerable.Max(selector). */
    @SuppressWarnings("unchecked")
    default <U extends Comparable<? super U>> T max(Function<? super T, ? extends U> selector) {
        T best = null;
        U bestKey = null;
        for (Object item : this) {
            T element = (T) item;
            U key = selector.apply(element);
            if (bestKey == null || key.compareTo(bestKey) > 0) {
                best = element;
                bestKey = key;
            }
        }
        if (best == null) {
            throw new IllegalStateException("The sequence did not contain any elements.");
        }
        return best;
    }

    /** C# Enumerable.Min(selector). */
    @SuppressWarnings("unchecked")
    default <U extends Comparable<? super U>> T min(Function<? super T, ? extends U> selector) {
        T best = null;
        U bestKey = null;
        for (Object item : this) {
            T element = (T) item;
            U key = selector.apply(element);
            if (bestKey == null || key.compareTo(bestKey) < 0) {
                best = element;
                bestKey = key;
            }
        }
        if (best == null) {
            throw new IllegalStateException("The sequence did not contain any elements.");
        }
        return best;
    }

    /** C# Enumerable.Max(comparator). */
    @SuppressWarnings("unchecked")
    default T max(Comparator<? super T> comparator) {
        T best = null;
        for (Object item : this) {
            T element = (T) item;
            if (best == null || comparator.compare(element, best) > 0) {
                best = element;
            }
        }
        if (best == null) {
            throw new IllegalStateException("The sequence did not contain any elements.");
        }
        return best;
    }

    /** C# Enumerable.Min(comparator). */
    @SuppressWarnings("unchecked")
    default T min(Comparator<? super T> comparator) {
        T best = null;
        for (Object item : this) {
            T element = (T) item;
            if (best == null || comparator.compare(element, best) < 0) {
                best = element;
            }
        }
        if (best == null) {
            throw new IllegalStateException("The sequence did not contain any elements.");
        }
        return best;
    }

    /** C# Enumerable.Any(predicate). */
    @SuppressWarnings("unchecked")
    default boolean anyMatch(Predicate<? super T> predicate) {
        for (Object item : this) {
            if (predicate.test((T) item)) {
                return true;
            }
        }
        return false;
    }

    default int add(Object value) { throw new UnsupportedOperationException(); }
    default void clear() { throw new UnsupportedOperationException(); }
    default boolean contains(Object value) { throw new UnsupportedOperationException(); }
    default int indexOf(Object value) { throw new UnsupportedOperationException(); }
    default void insert(int index, Object value) { throw new UnsupportedOperationException(); }
    default boolean getIsFixedSize() { return false; }
    default boolean getIsReadOnly() { return false; }
    default boolean remove(Object value) { throw new UnsupportedOperationException(); }
    default Object removeAt(int index) { throw new UnsupportedOperationException(); }
    default T get(int index) { throw new UnsupportedOperationException(); }
    default Object set(int index, Object value) { throw new UnsupportedOperationException(); }

    /**
     * Typed identity bridge for an already-IList value: keeps the element type
     * visible to generated {@code var} inference (a raw CSharpIList makes
     * {@code get(0)} return Object, e.g. IntersectionInfo.Par0 call sites).
     */
    @SuppressWarnings("unchecked")
    static <T> CSharpIList<T> from(CSharpIList<T> list) {
        return (CSharpIList<T>) list;
    }

    /**
     * Adapts a generic list (CSharpList / CSharpGenericIList) to the non-generic
     * C# IList surface. C# allows passing a List&lt;T&gt; where IList is expected;
     * the Java interfaces are unrelated, so the converter wraps the argument or
     * cast with this bridge. add(Object) returns the insertion position like C#
     * IList.Add.
     */
    @SuppressWarnings({ "unchecked", "rawtypes" })
    static CSharpIList from(CSharpGenericIList<?> list) {
        if (list == null) {
            return null;
        }
        return new CSharpIList() {
            @Override
            public int add(Object value) {
                int count = ((CSharpGenericIList) list).getCount();
                if (((CSharpGenericIList) list).add(value)) {
                    return count;
                }
                return -1;
            }
            @Override
            public void clear() {
                list.clear();
            }
            @Override
            public boolean contains(Object value) {
                return list.contains(value);
            }
            @Override
            public int indexOf(Object value) {
                return list.indexOf(value);
            }
            @Override
            public void insert(int index, Object value) {
                ((CSharpGenericIList) list).insert(index, value);
            }
            @Override
            public boolean getIsFixedSize() {
                return false;
            }
            @Override
            public boolean getIsReadOnly() {
                return list.getIsReadOnly();
            }
            @Override
            public boolean remove(Object value) {
                return ((CSharpGenericIList) list).remove(value);
            }
            @Override
            public Object removeAt(int index) {
                list.removeAt(index);
                return null;
            }
            @Override
            public Object get(int index) {
                return list.get(index);
            }
            @Override
            public Object set(int index, Object value) {
                return ((CSharpGenericIList) list).set(index, value);
            }
            public void copyTo(CSharpArray array, int index) {
                int i = 0;
                for (Object element : list) {
                    array.setValue(element, index + i);
                    i++;
                }
            }
            @Override
            public boolean getIsSynchronized() {
                return false;
            }
            @Override
            public Object getSyncRoot() {
                return list;
            }
            @Override
            public int getCount() {
                return list.getCount();
            }
            @Override
            public CSharpGenericEnumerator<Object> iterator() {
                return CSharpGenericEnumerator.from((java.util.Iterator) list.iterator());
            }
        };
    }

    /**
     * Adapts a CSharpICollection value (the views returned by the compat
     * SortedList.Values / Hashtable.Keys / IDictionary member mappings) to the
     * non-generic C# IList surface. A C# System.Collections.ICollection value
     * assigns to any IList-family slot, but the two compat interface families
     * cannot share a type (C# IList.Add yields int while ICollection&lt;T&gt;.Add
     * yields boolean), so the converter wraps the value with this read-through
     * view. Mutators keep the throwing defaults, matching the
     * NotSupportedException the .NET dictionary key/value views throw;
     * Count/CopyTo/enumeration (the only surface a non-generic C# ICollection
     * slot can use) delegate to the wrapped collection.
     */
    /**
     * Adapts a CSharpCollection implementer (C# non-generic IList view such as
     * XmlAttributeCollection, whose Java class implements CSharpCollection after
     * the ICollection-diamond narrowing) to the non-generic CSharpIList surface.
     * Read surface delegates (Count/CopyTo/enumeration); IList mutators keep the
     * interface defaults (throwing), matching the C# read-only view.
     * <p>If the collection already implements CSharpIList (e.g. CaptureCollection /
     * GroupCollection / MatchCollection, which implement both the generic and
     * non-generic C# IList interfaces), return it directly — wrapping would lose
     * the class's own get/contains/indexOf/getIsReadOnly implementations and fall
     * back to the interface defaults (UnsupportedOperationException / false).
     */
    @SuppressWarnings("unchecked")
    static CSharpIList from(CSharpCollection collection) {
        if (collection instanceof CSharpIList) {
            return (CSharpIList) collection;
        }
        return new CSharpIList() {
            @Override
            public CSharpGenericEnumerator iterator() {
                return collection.iterator();
            }
            @Override
            public int getCount() {
                return collection.getCount();
            }
            @Override
            public void copyTo(Object array, int index) {
                collection.copyTo(array, index);
            }
            @Override
            public void copyTo(CSharpArray array, int index) {
                collection.copyTo(array, index);
            }
            @Override
            public boolean getIsSynchronized() {
                return collection.getIsSynchronized();
            }
            @Override
            public Object getSyncRoot() {
                return collection.getSyncRoot();
            }
        };
    }

    static CSharpIList from(CSharpICollection<?> collection) {
        if (collection == null) {
            return null;
        }
        if (collection instanceof CSharpIList) {
            return (CSharpIList) collection;
        }
        return new CSharpIList() {
            @Override
            public int getCount() {
                return collection.getCount();
            }
            @Override
            public CSharpGenericEnumerator iterator() {
                return collection.iterator();
            }
            @Override
            public boolean getIsSynchronized() {
                return collection.getIsSynchronized();
            }
            @Override
            public Object getSyncRoot() {
                return collection.getSyncRoot();
            }
            @Override
            public void copyTo(CSharpArray array, int index) {
                collection.copyTo(array, index);
            }
        };
    }
}
