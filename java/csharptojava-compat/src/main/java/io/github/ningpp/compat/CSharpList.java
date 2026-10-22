package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Comparator;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.stream.Collector;
import java.util.stream.Collectors;

public class CSharpList<T> extends ArrayList<T> implements CSharpGenericIList<T>, CSharpReadOnlyList<T>, Cloneable {

    public static <T> Collector<T, ?, CSharpList<T>> toCSharpList() {
        return Collectors.toCollection(() -> new CSharpList<>());
    }

    public CSharpList() {
        super();
    }

    public CSharpList(int capacity) {
        super(capacity);
    }

    public CSharpList(Collection<? extends T> c) {
        super(c);
    }

    /**
     * C# {@code new List<KeyValuePair<K, V>>(dictionary)}: a C# Dictionary&lt;K,V&gt;
     * implements IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;, so the collection-constructor
     * overload accepts it; compat maps are {@code Iterable} over their entries but
     * deliberately not {@link Collection}s, so they need their own constructor that
     * walks the pairs. The same bridge serves every other compat type that is
     * C#-enumerable without being a Java collection.
     *
     * <p>Overload-resolution invariant: no compat class may implement both
     * {@code CSharpIterable} and {@link Collection} — an argument doing both would
     * match this constructor and {@link #CSharpList(Collection)} equally, making the
     * call ambiguous. Today the families are cleanly split (collections via
     * {@code CSharpGenericIterable}, maps/stack/queue via {@code CSharpIterable}).
     */
    public CSharpList(CSharpIterable<? extends T> pairs) {
        for (T pair : pairs) {
            add(pair);
        }
    }

    /**
     * C# {@code new List<T>(T[] values)}: a C# array implements {@code IEnumerable<T>},
     * so the collection-constructor overload accepts it; a Java reference array is not
     * a {@link Collection}, so it needs its own constructor.
     */
    public CSharpList(T[] values) {
        super(Math.max(values.length, 0));
        Collections.addAll(this, values);
    }

    // C# `new List<int>(int[])` and friends: primitive arrays implement IEnumerable<T>
    // on the CLR but Java primitive arrays implement neither Collection nor Iterable,
    // so each primitive domain needs an explicit boxing constructor.

    @SuppressWarnings("unchecked")
    public CSharpList(int[] values) {
        super(Math.max(values.length, 0));
        for (int value : values) add((T) Integer.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(long[] values) {
        super(Math.max(values.length, 0));
        for (long value : values) add((T) Long.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(short[] values) {
        super(Math.max(values.length, 0));
        for (short value : values) add((T) Short.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(byte[] values) {
        super(Math.max(values.length, 0));
        for (byte value : values) add((T) Byte.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(char[] values) {
        super(Math.max(values.length, 0));
        for (char value : values) add((T) Character.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(float[] values) {
        super(Math.max(values.length, 0));
        for (float value : values) add((T) Float.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(double[] values) {
        super(Math.max(values.length, 0));
        for (double value : values) add((T) Double.valueOf(value));
    }

    @SuppressWarnings("unchecked")
    public CSharpList(boolean[] values) {
        super(Math.max(values.length, 0));
        for (boolean value : values) add((T) Boolean.valueOf(value));
    }

    /**
     * C# List&lt;T&gt;[index] throws ArgumentOutOfRangeException on out-of-bounds
     * access, whereas Java's ArrayList.get(index) throws IndexOutOfBoundsException.
     * Override to preserve the C# exception type so tests expecting
     * ArgumentOutOfRangeException pass correctly.
     */
    @Override
    public T get(int index) {
        if (index < 0 || index >= size()) {
            throw new ArgumentOutOfRangeException(
                "index", index,
                "Index was out of range. Must be non-negative and less than the size of the collection.");
        }
        return super.get(index);
    }

    // CSharpGenericIList / CSharpICollection methods

    @Override
    public void copyTo(T[] array, int arrayIndex) {
        for (int i = 0; i < size(); i++) {
            array[arrayIndex + i] = get(i);
        }
    }

    /** C# ICollection.CopyTo(Array, int) — compat array view with validation. */
    public void copyTo(CSharpArray array, int index) {
        if (array == null) {
            throw new ArgumentNullException("array");
        }
        if (array.hasNonZeroLowerBound()) {
            throw new ArgumentOutOfRangeException("destinationIndex");
        }
        if (index < 0) {
            throw new ArgumentOutOfRangeException("destinationIndex");
        }
        if (index > array.getLength() - size()) {
            throw new ArgumentException("Destination array was not long enough.");
        }
        for (int i = 0; i < size(); i++) {
            array.setValue(get(i), index + i);
        }
    }

    /**
     * Copies the elements of a CSharpList&lt;Integer&gt; to a primitive int[] array,
     * performing auto-unboxing. Mirrors the C# List&lt;int&gt;.CopyTo(int[]) pattern.
     */
    public void copyTo(int[] array, int arrayIndex) {
        for (int i = 0; i < size(); i++) {
            array[arrayIndex + i] = (Integer) get(i);
        }
    }

    /**
     * Copies all elements of a CSharpList&lt;Integer&gt; to a primitive int[] array
     * starting at index 0.
     */
    public void copyTo(int[] array) {
        copyTo(array, 0);
    }

    @Override
    public int getCount() {
        return size();
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public void insert(int index, T item) {
        add(index, item);
    }

    @Override
    public void removeAt(int index) {
        remove(index);
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        // Index-based enumerator instead of wrapping the ArrayList iterator so that
        // reset() restarts the enumeration (C# List<T>.GetEnumerator().Reset()).
        return new CSharpGenericEnumerator<T>() {
            private int index = -1;

            @Override
            public boolean moveNext() {
                int size = size();
                if (index >= size) {
                    return false;
                }
                index++;
                return index < size;
            }

            @Override
            public T getCurrent() {
                if (index < 0 || index >= size()) {
                    throw new IllegalStateException("Enumeration has not started or has already finished.");
                }
                return get(index);
            }

            @Override
            public boolean hasNext() {
                return index + 1 < size();
            }

            @Override
            public T next() {
                if (!hasNext()) {
                    throw new java.util.NoSuchElementException();
                }
                index++;
                return get(index);
            }

            @Override
            public void reset() {
                index = -1;
            }

            @Override
            public void remove() {
                if (index < 0 || index >= size()) {
                    throw new IllegalStateException();
                }
                CSharpList.this.remove(index);
                index--;
            }
        };
    }

    // List-specific methods

    public void addRange(Collection<? extends T> c) {
        addAll(c);
    }

    public void addRange(T[] arr) {
        for (T item : arr) add(item);
    }

    /** C# List&lt;byte&gt;.AddRange(byte[]) — boxes each byte as Integer. */
    public void addRange(byte[] arr) {
        for (byte item : arr) {
            @SuppressWarnings("unchecked")
            T boxed = (T) Integer.valueOf(item & 0xFF);
            add(boxed);
        }
    }

    public CSharpReadOnlyList<T> asReadOnly() {
        return new CSharpReadOnlyList<T>() {
            private final CSharpList<T> source = CSharpList.this;

            @Override
            public T get(int index) {
                return source.get(index);
            }

            @Override
            public int getCount() {
                return source.getCount();
            }

            @Override
            public CSharpGenericEnumerator<T> iterator() {
                return source.iterator();
            }
        };
    }

    public int binarySearch(T item) {
        return Collections.binarySearch(this, item, (a, b) -> {
            @SuppressWarnings("unchecked")
            Comparable<Object> ca = (Comparable<Object>) a;
            return ca.compareTo(b);
        });
    }

    public int binarySearch(T item, CSharpGenericComparer<T> comparer) {
        return Collections.binarySearch(this, item, (a, b) -> comparer.compare(a, b));
    }

    public int binarySearch(int index, int count, T item, CSharpGenericComparer<T> comparer) {
        List<T> subList = subList(index, index + count);
        return Collections.binarySearch(subList, item, (a, b) -> comparer.compare(a, b)) + index;
    }

    public int ensureCapacityCSharp(int minCapacity) {
        super.ensureCapacity(minCapacity);
        return getCapacity();
    }

    public boolean exists(Predicate<? super T> match) {
        for (T item : this) {
            if (match.test(item)) return true;
        }
        return false;
    }

    public T find(Predicate<? super T> match) {
        for (T item : this) {
            if (match.test(item)) return item;
        }
        return null;
    }

    public CSharpList<T> findAll(Predicate<? super T> match) {
        CSharpList<T> result = new CSharpList<>();
        for (T item : this) {
            if (match.test(item)) result.add(item);
        }
        return result;
    }

    public int findIndex(Predicate<? super T> match) {
        for (int i = 0; i < size(); i++) {
            if (match.test(get(i))) return i;
        }
        return -1;
    }

    public int findIndex(int startIndex, Predicate<? super T> match) {
        for (int i = startIndex; i < size(); i++) {
            if (match.test(get(i))) return i;
        }
        return -1;
    }

    public int findIndex(int startIndex, int count, Predicate<? super T> match) {
        int end = startIndex + count;
        for (int i = startIndex; i < end && i < size(); i++) {
            if (match.test(get(i))) return i;
        }
        return -1;
    }

    public T findLast(Predicate<? super T> match) {
        for (int i = size() - 1; i >= 0; i--) {
            if (match.test(get(i))) return get(i);
        }
        return null;
    }

    public int findLastIndex(Predicate<? super T> match) {
        for (int i = size() - 1; i >= 0; i--) {
            if (match.test(get(i))) return i;
        }
        return -1;
    }

    public void forEach(Consumer<? super T> action) {
        super.forEach(action);
    }

    public CSharpList<T> getRange(int index, int count) {
        return new CSharpList<>(subList(index, index + count));
    }

    public int indexOf(Object o, int start) {
        for (int i = start; i < size(); i++) {
            if (Objects.equals(get(i), o)) return i;
        }
        return -1;
    }

    public int indexOf(Object o, int start, int count) {
        int end = Math.min(start + count, size());
        for (int i = start; i < end; i++) {
            if (Objects.equals(get(i), o)) return i;
        }
        return -1;
    }

    public void insertRange(int index, Collection<? extends T> c) {
        int i = index;
        for (T item : c) {
            add(i++, item);
        }
    }

    public void insertRange(int index, T[] arr) {
        int i = index;
        for (T item : arr) {
            add(i++, item);
        }
    }

    public int removeAllMatching(Predicate<? super T> match) {
        int removed = 0;
        Iterator<T> it = iterator();
        while (it.hasNext()) {
            if (match.test(it.next())) {
                it.remove();
                removed++;
            }
        }
        return removed;
    }

    // C# List<T>.RemoveRange(index, count) - use _removeRange for C# semantics
    // Note: do NOT override removeRange(int, int) with C# (index, count) semantics,
    // because ArrayList.SubList.removeRange delegates here with Java (fromIndex, toIndex) semantics.
    public void _removeRange(int index, int count) {
        super.removeRange(index, index + count);
    }

    public void reverse() {
        Collections.reverse(this);
    }

    public void reverse(int index, int count) {
        for (int i = 0; i < count / 2; i++) {
            int a = index + i;
            int b = index + count - 1 - i;
            T tmp = get(a);
            set(a, get(b));
            set(b, tmp);
        }
    }

    public void sort() {
        super.sort(null);
    }

    /**
     * Handles sort with a Comparator to deal with non-transitive comparators
     * that Java's TimSort rejects. C# List.Sort uses introsort which does
     * not enforce the transitivity contract. When TimSort throws
     * IllegalArgumentException, we fall back to insertion sort.
     */
    @Override
    public void sort(Comparator<? super T> c) {
        if (c == null) {
            super.sort(null);
            return;
        }
        try {
            super.sort(c);
        } catch (IllegalArgumentException e) {
            insertionSort(c);
        }
    }

    public void sort(CSharpGenericComparer<T> comparer) {
        sort((Comparator<T>) (a, b) -> comparer.compare(a, b));
    }

    private void insertionSort(Comparator<? super T> c) {
        int n = size();
        for (int i = 1; i < n; i++) {
            T key = get(i);
            int j = i - 1;
            while (j >= 0 && c.compare(get(j), key) > 0) {
                set(j + 1, get(j));
                j--;
            }
            set(j + 1, key);
        }
    }

    /** Mirrors C# List&lt;T&gt;.ConvertAll(Converter&lt;T, U&gt;) — maps every element. */
    public <U> CSharpList<U> convertAll(Converter<? super T, ? extends U> converter) {
        if (converter == null) {
            throw new NullPointerException("converter");
        }
        CSharpList<U> result = new CSharpList<>(size());
        for (T item : this) {
            result.add(converter.convert(item));
        }
        return result;
    }

    public void trimExcess() {
        super.trimToSize();
    }

    public boolean trueForAll(Predicate<? super T> match) {
        for (T item : this) {
            if (!match.test(item)) return false;
        }
        return true;
    }

    // Properties

    public int getCapacity() {
        return -1; // ArrayList doesn't expose capacity
    }

    @Override
    public CSharpList<T> clone() {
        return new CSharpList<>(this);
    }

    /** C# LINQ Single() — returns the only element or throws. */
    public T single() {
        if (size() != 1) {
            throw new IllegalStateException("The sequence contains more than one element.");
        }
        return get(0);
    }
}

