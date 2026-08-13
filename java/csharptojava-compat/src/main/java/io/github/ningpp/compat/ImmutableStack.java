package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.Deque;

/**
 * Compat for System.Collections.Immutable.ImmutableStack&lt;T&gt;.
 */
public class ImmutableStack<T> implements IImmutableStack<T> {
    private final Deque<T> items;

    public ImmutableStack() {
        this.items = new ArrayDeque<>();
    }

    private ImmutableStack(Deque<T> items) {
        this.items = items;
    }

    public static <T> ImmutableStack<T> createRange(Iterable<T> source) {
        Deque<T> stack = new ArrayDeque<>();
        for (T item : source) {
            stack.addFirst(item);
        }
        return new ImmutableStack<>(stack);
    }

    public static <T> ImmutableStack<T> createRange(CSharpGenericIterable<T> source) {
        Deque<T> stack = new ArrayDeque<>();
        for (T item : source) {
            stack.addFirst(item);
        }
        return new ImmutableStack<>(stack);
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

    public T peek() {
        return items.peekFirst();
    }

    public ImmutableStack<T> pop() {
        Deque<T> copy = new ArrayDeque<>(items);
        copy.pollFirst();
        return new ImmutableStack<>(copy);
    }

    public ImmutableStack<T> push(T item) {
        Deque<T> copy = new ArrayDeque<>(items);
        copy.addFirst(item);
        return new ImmutableStack<>(copy);
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
