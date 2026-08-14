package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableQueue&lt;T&gt;.
 */
public interface IImmutableQueue<T> extends Iterable<T> {
    default int size() {
        int count = 0;
        for (T ignored : this) {
            count++;
        }
        return count;
    }

    default boolean isEmpty() {
        return !iterator().hasNext();
    }

    /** C# IImmutableQueue&lt;T&gt;.IsEmpty — generated getter form. */
    default boolean getIsEmpty() {
        return isEmpty();
    }

    T peek();

    /** C# IImmutableQueue&lt;T&gt;.Clear() — returns an empty queue. */
    default IImmutableQueue<T> clear() {
        return this;
    }

    /** C# IImmutableQueue&lt;T&gt;.Dequeue() — returns the queue without the head. */
    default IImmutableQueue<T> dequeue() {
        return this;
    }

    /** C# IImmutableQueue&lt;T&gt;.Enqueue(T) — returns the queue with the value appended. */
    default IImmutableQueue<T> enqueue(T value) {
        return this;
    }
}
