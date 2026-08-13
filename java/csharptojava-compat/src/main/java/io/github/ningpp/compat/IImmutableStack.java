package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableStack&lt;T&gt;.
 */
public interface IImmutableStack<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    T peek();
}
