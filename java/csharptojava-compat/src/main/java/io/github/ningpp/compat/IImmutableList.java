package io.github.ningpp.compat;

import java.util.List;

/**
 * Compat for System.Collections.Immutable.IImmutableList&lt;T&gt;.
 */
public interface IImmutableList<T> extends Iterable<T> {
    int size();

    boolean isEmpty();

    T get(int index);

    List<T> toList();
}
