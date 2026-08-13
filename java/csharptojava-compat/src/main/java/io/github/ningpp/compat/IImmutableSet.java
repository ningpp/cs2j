package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Immutable.IImmutableSet&lt;T&gt;.
 */
public interface IImmutableSet<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    boolean contains(T item);
}
