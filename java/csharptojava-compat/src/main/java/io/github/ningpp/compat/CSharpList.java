package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.stream.Collector;
import java.util.stream.Collectors;

public class CSharpList<T> extends ArrayList<T> implements CSharpGenericIList<T>, Cloneable {

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

    // CSharpGenericIList / CSharpICollection methods

    @Override
    public void copyTo(T[] array, int arrayIndex) {
        for (int i = 0; i < size(); i++) {
            array[arrayIndex + i] = get(i);
        }
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
        return CSharpGenericEnumerator.from(super.iterator());
    }

    // List-specific methods

    public void addRange(Collection<? extends T> c) {
        addAll(c);
    }

    public void addRange(T[] arr) {
        for (T item : arr) add(item);
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

    public void sort(CSharpGenericComparer<T> comparer) {
        super.sort((a, b) -> comparer.compare(a, b));
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
}
