package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.Deque;

/**
 * Compat for System.Collections.Immutable.ImmutableQueue&lt;T&gt;.
 */
public class ImmutableQueue<T> implements IImmutableQueue<T> {
    private final Deque<T> items;

    public ImmutableQueue() {
        this.items = new ArrayDeque<>();
    }

    private ImmutableQueue(Deque<T> items) {
        this.items = items;
    }

    /** C# ImmutableQueue.Create&lt;T&gt;() — an empty immutable queue. */
    public static <T> ImmutableQueue<T> create() {
        return new ImmutableQueue<>();
    }

    public static <T> ImmutableQueue<T> createRange(Iterable<T> source) {
        Deque<T> queue = new ArrayDeque<>();
        for (T item : source) {
            queue.addLast(item);
        }
        return new ImmutableQueue<>(queue);
    }

    public static <T> ImmutableQueue<T> createRange(CSharpGenericIterable<T> source) {
        Deque<T> queue = new ArrayDeque<>();
        for (T item : source) {
            queue.addLast(item);
        }
        return new ImmutableQueue<>(queue);
    }

    public int size() {
        return items.size();
    }

    public int getCount() {
        return items.size();
    }

    public boolean isEmpty() {
        return items.isEmpty();
    }

    /** C# ImmutableQueue&lt;T&gt;.IsEmpty. */
    public boolean getIsEmpty() {
        return items.isEmpty();
    }

    public T peek() {
        return items.peekFirst();
    }

    /** C# ImmutableQueue&lt;T&gt;.Clear() — returns an empty queue. */
    public ImmutableQueue<T> clear() {
        return new ImmutableQueue<>();
    }

    public ImmutableQueue<T> dequeue() {
        Deque<T> copy = new ArrayDeque<>(items);
        copy.pollFirst();
        return new ImmutableQueue<>(copy);
    }

    public ImmutableQueue<T> enqueue(T item) {
        Deque<T> copy = new ArrayDeque<>(items);
        copy.addLast(item);
        return new ImmutableQueue<>(copy);
    }

    @Override
    public java.util.Iterator<T> iterator() {
        return items.iterator();
    }

    @Override
    public String toString() {
        return items.toString();
    }
}
