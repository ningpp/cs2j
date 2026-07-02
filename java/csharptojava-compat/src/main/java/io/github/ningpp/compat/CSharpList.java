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

public class CSharpList<T> implements CSharpGenericIList<T>, Cloneable {
    private final ArrayList<T> list;

    public CSharpList() {
        this.list = new ArrayList<>();
    }

    public CSharpList(int capacity) {
        this.list = new ArrayList<>(capacity);
    }

    public CSharpList(Collection<? extends T> c) {
        this.list = new ArrayList<>(c);
    }

    // CSharpGenericIList / CSharpICollection methods

    @Override
    public boolean add(T item) {
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
    public void copyTo(T[] array, int arrayIndex) {
        for (int i = 0; i < list.size(); i++) {
            array[arrayIndex + i] = list.get(i);
        }
    }

    @Override
    public boolean remove(Object o) {
        return list.remove(o);
    }

    @Override
    public int getCount() {
        return list.size();
    }

    @Override
    public boolean getIsReadOnly() {
        return false;
    }

    @Override
    public int indexOf(Object o) {
        return list.indexOf(o);
    }

    @Override
    public void insert(int index, T item) {
        list.add(index, item);
    }

    @Override
    public void removeAt(int index) {
        list.remove(index);
    }

    @Override
    public T get(int index) {
        return list.get(index);
    }

    @Override
    public T set(int index, T value) {
        return list.set(index, value);
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(list.iterator());
    }

    // List-specific methods

    public void addRange(Collection<? extends T> c) {
        list.addAll(c);
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
        return Collections.binarySearch(list, item, (a, b) -> {
            @SuppressWarnings("unchecked")
            Comparable<Object> ca = (Comparable<Object>) a;
            return ca.compareTo(b);
        });
    }

    public int binarySearch(T item, CSharpGenericComparer<T> comparer) {
        return Collections.binarySearch(list, item, (a, b) -> comparer.compare(a, b));
    }

    public int binarySearch(int index, int count, T item, CSharpGenericComparer<T> comparer) {
        List<T> subList = list.subList(index, index + count);
        return Collections.binarySearch(subList, item, (a, b) -> comparer.compare(a, b)) + index;
    }

    public int ensureCapacity(int minCapacity) {
        list.ensureCapacity(minCapacity);
        return getCapacity();
    }

    public boolean exists(Predicate<? super T> match) {
        for (T item : list) {
            if (match.test(item)) return true;
        }
        return false;
    }

    public T find(Predicate<? super T> match) {
        for (T item : list) {
            if (match.test(item)) return item;
        }
        return null;
    }

    public CSharpList<T> findAll(Predicate<? super T> match) {
        CSharpList<T> result = new CSharpList<>();
        for (T item : list) {
            if (match.test(item)) result.add(item);
        }
        return result;
    }

    public int findIndex(Predicate<? super T> match) {
        for (int i = 0; i < list.size(); i++) {
            if (match.test(list.get(i))) return i;
        }
        return -1;
    }

    public int findIndex(int startIndex, Predicate<? super T> match) {
        for (int i = startIndex; i < list.size(); i++) {
            if (match.test(list.get(i))) return i;
        }
        return -1;
    }

    public int findIndex(int startIndex, int count, Predicate<? super T> match) {
        int end = startIndex + count;
        for (int i = startIndex; i < end && i < list.size(); i++) {
            if (match.test(list.get(i))) return i;
        }
        return -1;
    }

    public T findLast(Predicate<? super T> match) {
        for (int i = list.size() - 1; i >= 0; i--) {
            if (match.test(list.get(i))) return list.get(i);
        }
        return null;
    }

    public int findLastIndex(Predicate<? super T> match) {
        for (int i = list.size() - 1; i >= 0; i--) {
            if (match.test(list.get(i))) return i;
        }
        return -1;
    }

    public void forEach(Consumer<? super T> action) {
        list.forEach(action);
    }

    public CSharpList<T> getRange(int index, int count) {
        return new CSharpList<>(list.subList(index, index + count));
    }

    public int indexOf(Object o, int start) {
        for (int i = start; i < list.size(); i++) {
            if (Objects.equals(list.get(i), o)) return i;
        }
        return -1;
    }

    public int indexOf(Object o, int start, int count) {
        int end = Math.min(start + count, list.size());
        for (int i = start; i < end; i++) {
            if (Objects.equals(list.get(i), o)) return i;
        }
        return -1;
    }

    public void insertRange(int index, Collection<? extends T> c) {
        int i = index;
        for (T item : c) {
            list.add(i++, item);
        }
    }

    public int lastIndexOf(Object o) {
        return list.lastIndexOf(o);
    }

    public int removeAllMatching(Predicate<? super T> match) {
        int removed = 0;
        Iterator<T> it = list.iterator();
        while (it.hasNext()) {
            if (match.test(it.next())) {
                it.remove();
                removed++;
            }
        }
        return removed;
    }

    public void removeRange(int index, int count) {
        list.subList(index, index + count).clear();
    }

    public void reverse() {
        Collections.reverse(list);
    }

    public void reverse(int index, int count) {
        for (int i = 0; i < count / 2; i++) {
            int a = index + i;
            int b = index + count - 1 - i;
            T tmp = list.get(a);
            list.set(a, list.get(b));
            list.set(b, tmp);
        }
    }

    public void sort() {
        list.sort(null);
    }

    public void sort(CSharpGenericComparer<T> comparer) {
        list.sort((a, b) -> comparer.compare(a, b));
    }

    public Object[] toArray() {
        return list.toArray();
    }

    @SuppressWarnings("unchecked")
    public <E> E[] toArray(E[] a) {
        return list.toArray(a);
    }

    public void trimExcess() {
        list.trimToSize();
    }

    public boolean trueForAll(Predicate<? super T> match) {
        for (T item : list) {
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
        try {
            @SuppressWarnings("unchecked")
            CSharpList<T> c = (CSharpList<T>) super.clone();
            return new CSharpList<>(list);
        } catch (CloneNotSupportedException e) {
            throw new InternalError();
        }
    }
}
