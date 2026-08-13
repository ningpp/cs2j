package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableQueue&lt;T&gt;.
 */
public interface IImmutableQueue<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    T peek();
}
