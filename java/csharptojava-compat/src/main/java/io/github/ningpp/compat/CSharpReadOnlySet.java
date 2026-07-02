package io.github.ningpp.compat;

import java.util.Collection;

public interface CSharpReadOnlySet<T> extends CSharpReadOnlyCollection<T> {
    boolean contains(Object o);
    boolean isProperSubsetOf(Collection<? extends T> other);
    boolean isProperSupersetOf(Collection<? extends T> other);
    boolean isSubsetOf(Collection<? extends T> other);
    boolean isSupersetOf(Collection<? extends T> other);
    boolean overlaps(Collection<? extends T> other);
    boolean setEquals(Collection<? extends T> other);
}
