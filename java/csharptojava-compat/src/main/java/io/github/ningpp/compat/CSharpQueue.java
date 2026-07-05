package io.github.ningpp.compat;

import java.util.Collection;
import java.util.Iterator;
import java.util.LinkedList;
import java.util.NoSuchElementException;
import java.util.Objects;

public class CSharpQueue<T> implements CSharpGenericIterable<T>, Cloneable {
    private LinkedList<T> queue;

    public CSharpQueue() {
        this.queue = new LinkedList<>();
    }

    public CSharpQueue(int capacity) {
        this.queue = new LinkedList<>();
    }

    public CSharpQueue(Collection<? extends T> c) {
        this.queue = new LinkedList<>(c);
    }

    public void enqueue(T item) {
        queue.addLast(item);
    }

    public T dequeue() {
        if (queue.isEmpty()) {
            throw new NoSuchElementException("Queue is empty");
        }
        return queue.removeFirst();
    }

    public T peek() {
        if (queue.isEmpty()) {
            throw new NoSuchElementException("Queue is empty");
        }
        return queue.peekFirst();
    }

    public void clear() {
        queue.clear();
    }

    public boolean contains(Object o) {
        return queue.contains(o);
    }

    public void copyTo(T[] array, int arrayIndex) {
        Objects.checkIndex(arrayIndex, array.length + 1);
        if (arrayIndex + queue.size() > array.length) {
            throw new IndexOutOfBoundsException(
                "Destination array is not long enough to copy all elements.");
        }
        int i = arrayIndex;
        for (T item : queue) {
            array[i++] = item;
        }
    }

    public Object[] toArray() {
        return queue.toArray();
    }

    @Override
    @SuppressWarnings("unchecked")
    public <E> E[] toArray(E[] a) {
        return queue.toArray(a);
    }

    public void trimExcess() {
        // LinkedList has no capacity concept; no-op
    }

    public int ensureCapacity(int capacity) {
        // LinkedList has no capacity concept; no-op
        return queue.size();
    }

    public T tryDequeue() {
        if (queue.isEmpty()) {
            return null;
        }
        return queue.removeFirst();
    }

    public T tryPeek() {
        if (queue.isEmpty()) {
            return null;
        }
        return queue.peekFirst();
    }

    public int getCount() {
        return queue.size();
    }

    // Collection interface methods
    public int size() {
        return queue.size();
    }

    public boolean isEmpty() {
        return queue.isEmpty();
    }

    public boolean add(T e) {
        queue.addLast(e);
        return true;
    }

    public boolean remove(Object o) {
        return queue.remove(o);
    }

    public boolean containsAll(Collection<?> c) {
        return queue.containsAll(c);
    }

    public boolean addAll(Collection<? extends T> c) {
        return queue.addAll(c);
    }

    public boolean removeAll(Collection<?> c) {
        return queue.removeAll(c);
    }

    public boolean retainAll(Collection<?> c) {
        return queue.retainAll(c);
    }

    public T poll() {
        return tryDequeue();
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(queue.iterator());
    }

    @Override
    @SuppressWarnings("unchecked")
    public CSharpQueue<T> clone() {
        try {
            CSharpQueue<T> c = (CSharpQueue<T>) super.clone();
            c.queue = new LinkedList<>(this.queue);
            return c;
        } catch (CloneNotSupportedException e) {
            throw new InternalError();
        }
    }
}
