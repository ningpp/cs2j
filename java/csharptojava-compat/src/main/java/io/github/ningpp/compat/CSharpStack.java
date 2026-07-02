package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.Collection;
import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.Objects;
import java.util.Spliterator;
import java.util.Spliterators;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;

public class CSharpStack<T> implements Iterable<T> {
    private ArrayDeque<T> deque;

    public CSharpStack() {
        this.deque = new ArrayDeque<>();
    }

    public CSharpStack(int capacity) {
        this.deque = new ArrayDeque<>(capacity);
    }

    public CSharpStack(Collection<? extends T> c) {
        this.deque = new ArrayDeque<>(c.size());
        for (T item : c) {
            deque.addLast(item);
        }
    }

    public void push(T item) {
        deque.addLast(item);
    }

    public T pop() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException();
        }
        return deque.removeLast();
    }

    public T peek() {
        if (deque.isEmpty()) {
            throw new NoSuchElementException();
        }
        return deque.peekLast();
    }

    public int size() {
        return deque.size();
    }

    public boolean isEmpty() {
        return deque.isEmpty();
    }

    public void clear() {
        deque.clear();
    }

    public int ensureCapacity(int capacity) {
        if (capacity < 0) {
            throw new IllegalArgumentException("capacity must be non-negative");
        }
        if (capacity > deque.size()) {
            int targetCapacity = Math.max(capacity, deque.size());
            ArrayDeque<T> newDeque = new ArrayDeque<>(targetCapacity);
            newDeque.addAll(deque);
            deque = newDeque;
        }
        return capacity;
    }

    public boolean contains(Object item) {
        return deque.contains(item);
    }

    @Override
    public Iterator<T> iterator() {
        return deque.descendingIterator();
    }

    @Override
    public Spliterator<T> spliterator() {
        return Spliterators.spliteratorUnknownSize(iterator(), Spliterator.ORDERED);
    }

    public Stream<T> stream() {
        return StreamSupport.stream(spliterator(), false);
    }

    public Object[] toArray() {
        Object[] result = new Object[deque.size()];
        int i = 0;
        for (T item : this) {
            result[i++] = item;
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    public T[] toArray(T[] a) {
        int size = deque.size();
        T[] result = a.length >= size
            ? a
            : (T[]) java.lang.reflect.Array.newInstance(a.getClass().getComponentType(), size);
        int i = 0;
        for (T item : this) {
            result[i++] = item;
        }
        if (result.length > size) {
            result[size] = null;
        }
        return result;
    }

    public void copyTo(T[] array, int arrayIndex) {
        Objects.checkIndex(arrayIndex, array.length + 1);
        if (arrayIndex + deque.size() > array.length) {
            throw new IndexOutOfBoundsException(
                "Destination array is not long enough to copy all elements.");
        }
        int i = arrayIndex;
        for (T item : this) {
            array[i++] = item;
        }
    }

    public boolean tryPeek(ObjectHolder<T> holder) {
        if (deque.isEmpty()) {
            return false;
        }
        holder.value = deque.peekLast();
        return true;
    }

    public boolean tryPop(ObjectHolder<T> holder) {
        if (deque.isEmpty()) {
            return false;
        }
        holder.value = deque.removeLast();
        return true;
    }

    public void trimExcess() {
        ArrayDeque<T> newDeque = new ArrayDeque<>(deque);
        deque = newDeque;
    }

    public void trimExcess(int capacity) {
        if (capacity < deque.size()) {
            throw new IllegalArgumentException(
                "capacity must be >= current size");
        }
        ArrayDeque<T> newDeque = new ArrayDeque<>(capacity);
        newDeque.addAll(deque);
        deque = newDeque;
    }

    @SuppressWarnings("unchecked")
    public CSharpEnumerator getEnumerator() {
        return CSharpEnumerator.from((Iterator<Object>) (Iterator<?>) iterator());
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpStack)) return false;
        CSharpStack<?> other = (CSharpStack<?>) obj;
        if (this.size() != other.size()) return false;
        Iterator<T> it1 = this.iterator();
        Iterator<?> it2 = other.iterator();
        while (it1.hasNext() && it2.hasNext()) {
            if (!Objects.equals(it1.next(), it2.next())) return false;
        }
        return true;
    }

    @Override
    public int hashCode() {
        int h = 1;
        for (T item : this) {
            h = 31 * h + Objects.hashCode(item);
        }
        return h;
    }
}
