package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableStack&lt;T&gt;.
 */
public interface IImmutableStack<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

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