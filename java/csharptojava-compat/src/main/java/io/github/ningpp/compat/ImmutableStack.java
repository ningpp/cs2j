package io.github.ningpp.compat;

import java.util.ArrayDeque;
import java.util.Deque;

/**
 * Compat for System.Collections.Immutable.ImmutableStack&lt;T&gt;.
 */
public class ImmutableStack<T> implements IImmutableStack<T> {
    /** C# ImmutableStack.Empty — the empty singleton. */
    @SuppressWarnings({"rawtypes", "unchecked"})
    public static final ImmutableStack Empty = new ImmutableStack();

    private final Deque<T> items;

    public ImmutableStack() {
        this.items = new ArrayDeque<>();
    }

    private ImmutableStack(Deque<T> items) {
        this.items = items;
    }

    /** C# ImmutableStack.Create&lt;T&gt;() — an empty stack. */
    public static <T> ImmutableStack<T> create() {
        return new ImmutableStack<>();
    }

    /** C# ImmutableStack.Create&lt;T&gt;(item) / Create&lt;T&gt;(items). */
    @SafeVarargs
    public static <T> ImmutableStack<T> create(T... items) {
        ImmutableStack<T> stack = create();
        for (T item : items) {
            stack.items.addFirst(item);
        }
        return stack;
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

    /**
     * C#-named CreateRange used by the JSON immutable-collection factory, which
     * looks up a static generic method literally named "CreateRange" to build the
     * deserialized instance.
     */
    public static <T> ImmutableStack<T> CreateRange(CSharpGenericIterable<T> source) {
        return createRange(source);
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

    /** C# ImmutableStack&lt;T&gt;.IsEmpty. */
    public boolean getIsEmpty() {
        return items.isEmpty();
    }

    public T peek() {
        return items.peekFirst();
    }

    /** C# ImmutableStack&lt;T&gt;.Clear() — returns an empty stack. */
    public ImmutableStack<T> clear() {
        return new ImmutableStack<>();
    }

    public ImmutableStack<T> pop() {
        Deque<T> copy = new ArrayDeque<>(items);
        copy.pollFirst();
        return new ImmutableStack<>(copy);
    }

    /** C# ImmutableStack&lt;T&gt;.Pop(out T value) — pops and returns the value via holder. */
    public ImmutableStack<T> pop(ObjectHolder<T> value) {
        value.value = items.peekFirst();
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