package io.github.ningpp.compat;

import java.util.Collection;
import java.util.NoSuchElementException;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * Compat for System.Collections.Concurrent.ConcurrentQueue&lt;T&gt; — a FIFO,
 * thread-safe collection. Backed by ConcurrentLinkedQueue while keeping the
 * C# method surface (Enqueue/Dequeue/TryDequeue/Count/...) used by generated
 * code, mirroring {@link CSharpQueue} but without the non-thread-safe queue
 * implementation.
 */
public class ConcurrentQueue<T> implements CSharpGenericIterable<T> {
    private final ConcurrentLinkedQueue<T> queue = new ConcurrentLinkedQueue<>();

    public ConcurrentQueue() {
    }

    public ConcurrentQueue(Collection<? extends T> c) {
        if (c != null) {
            this.queue.addAll(c);
        }
    }

    /** C# new ConcurrentQueue&lt;T&gt;(IEnumerable&lt;T&gt;) / ConcurrentQueue&lt;T&gt;(T[]). */
    public ConcurrentQueue(T[] values) {
        if (values != null) {
            for (T value : values) {
                this.queue.add(value);
            }
        }
    }

    public void enqueue(T item) {
        queue.add(item);
    }

    public T dequeue() {
        T item = queue.poll();
        if (item == null && queue.isEmpty()) {
            throw new NoSuchElementException("Queue is empty");
        }
        return item;
    }

    public T peek() {
        T item = queue.peek();
        if (item == null && queue.isEmpty()) {
            throw new NoSuchElementException("Queue is empty");
        }
        return item;
    }

    public T tryDequeue() {
        return queue.poll();
    }

    public T tryPeek() {
        return queue.peek();
    }

    public boolean tryDequeue(ObjectHolder<T> outResult) {
        T item = queue.poll();
        if (item == null && queue.isEmpty()) {
            return false;
        }
        outResult.value = item;
        return true;
    }

    public boolean tryPeek(ObjectHolder<T> outResult) {
        T item = queue.peek();
        if (item == null && queue.isEmpty()) {
            return false;
        }
        outResult.value = item;
        return true;
    }

    public int getCount() {
        return queue.size();
    }

    public boolean isEmpty() {
        return queue.isEmpty();
    }

    public boolean contains(Object o) {
        return queue.contains(o);
    }

    public void clear() {
        queue.clear();
    }

    public Object[] toArray() {
        return queue.toArray();
    }

    @Override
    @SuppressWarnings("unchecked")
    public <E> E[] toArray(E[] a) {
        return queue.toArray(a);
    }

    public int size() {
        return queue.size();
    }

    public boolean add(T item) {
        queue.add(item);
        return true;
    }

    public boolean remove(Object o) {
        return queue.remove(o);
    }

    public T poll() {
        return queue.poll();
    }

    @Override
    public CSharpGenericEnumerator<T> iterator() {
        return CSharpGenericEnumerator.from(queue.iterator());
    }

    @Override
    public String toString() {
        return queue.toString();
    }
}
