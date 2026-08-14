package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableStack&lt;T&gt;.
 */
public interface IImmutableStack<T> extends Iterable<T> {
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

    /** C# IImmutableStack&lt;T&gt;.IsEmpty — generated getter form. */
    default boolean getIsEmpty() {
        return isEmpty();
    }

    T peek();

    default IImmutableStack<T> clear() {
        return this;
    }

    default IImmutableStack<T> pop() {
        return this;
    }

    default IImmutableStack<T> push(T value) {
        return this;
    }
}
